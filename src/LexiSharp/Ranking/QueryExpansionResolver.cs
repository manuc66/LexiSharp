using LexiSharp.Core;
using LexiSharp.Similarity;

namespace LexiSharp.Ranking;

/// <summary>
/// Resolves the <c>term*</c>/<c>term~N</c> atoms of one query against the index vocabulary: once per
/// <i>distinct</i> atom, and never more than <see cref="RankedTextSearchEngine.MaxExpansionAtoms"/>
/// distinct atoms per query.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the memo, in cost terms.</b> A prefix or fuzzy atom is resolved by walking the index
/// vocabulary, which for a corpus of a million distinct terms is a million comparisons — per atom.
/// The atom list comes from the query text, so a caller who repeats one atom fifty times used to buy
/// fifty walks for an answer identical to one. The cache is keyed on what resolution actually reads
/// (base term, kind, edit budget, field) and not on the whole record: a <c>+</c> or <c>-</c> decides
/// what the atom <i>gates on</i> and never what it resolves to, so <c>+catt~</c> and <c>catt~</c>
/// share one walk and each still receives the terms it asks for.
/// </para>
/// <para>
/// <b>Why the cap, in cost terms.</b> The memo removes repeats, not distinct atoms, and each distinct
/// atom is a full vocabulary walk. Without a bound, a query string is a multiplier on that walk:
/// measured on a 200 000-term vocabulary, one fuzzy atom cost 71 ms, fifty cost 1.1 s and two hundred
/// (2 199 characters, well inside a URL) cost 4.3 s — while matching nothing. The cap turns an
/// unbounded multiplier into a bounded one. It is refused rather than truncated because an atom
/// dropped silently is a query that searched for something other than what was asked.
/// </para>
/// <para>
/// The cap does not make a fuzzy atom cheap: one atom is still a walk of the vocabulary, and the
/// bound on what a single query can ask for is the cap, not the walk. Making the walk sub-linear is
/// a different change (a term index ordered for edit-distance search).
/// </para>
/// </remarks>
internal sealed class QueryExpansionResolver
{
    private readonly IVocabularyIndex? _vocabulary;
    private readonly IVocabularyPrefixIndex? _prefixIndex;
    private readonly bool _fuzzyOnlyOutOfVocabulary;
    private readonly Dictionary<QueryExpansion, string[]> _resolved = [];

    private int _distinctAtoms;

    /// <param name="vocabulary">
    /// The index vocabulary to expand against, or <c>null</c> when the index cannot enumerate one —
    /// an atom then resolves to its literal base term, which is the behavior of a plain query.
    /// </param>
    /// <param name="fuzzyOnlyOutOfVocabulary">
    /// When true, a fuzzy atom whose base term is already in the vocabulary resolves to that exact
    /// term instead of its near-variants.
    /// </param>
    public QueryExpansionResolver(IVocabularyIndex? vocabulary, bool fuzzyOnlyOutOfVocabulary)
    {
        _vocabulary = vocabulary;
        _prefixIndex = vocabulary as IVocabularyPrefixIndex;
        _fuzzyOnlyOutOfVocabulary = fuzzyOnlyOutOfVocabulary;
    }

    /// <summary>
    /// The terms <paramref name="expansion"/> resolves to, in resolution order: for a prefix atom,
    /// highest document frequency first then ordinal; for a fuzzy atom, closest first, then highest
    /// document frequency, then ordinal.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The query asks for more distinct atoms than <see cref="RankedTextSearchEngine.MaxExpansionAtoms"/>.
    /// </exception>
    public string[] Resolve(QueryExpansion expansion)
    {
        // An index that cannot enumerate its vocabulary answers every atom with its literal base —
        // no walk, so no atom budget is spent and no cap can be reached.
        if (_vocabulary is null)
            return [expansion.BaseTerm];

        QueryExpansion key = expansion with { Operator = QueryTermOperator.None };

        if (_resolved.TryGetValue(key, out string[]? cached))
            return cached;

        if (_distinctAtoms >= RankedTextSearchEngine.MaxExpansionAtoms)
        {
            throw new ArgumentException(
                $"A query may carry at most {RankedTextSearchEngine.MaxExpansionAtoms} distinct prefix/fuzzy " +
                "atoms (prefix and fuzzy terms are resolved one vocabulary walk each). Split the query, or " +
                "drop the repeated operators.");
        }

        _distinctAtoms++;

        var terms = new List<string>(4);

        if (expansion.Kind == QueryExpansionKind.Prefix)
            ExpandPrefix(terms, expansion);
        else if (_fuzzyOnlyOutOfVocabulary && _vocabulary.DocumentFrequency(expansion.BaseTerm) > 0)
            terms.Add(expansion.BaseTerm);
        else
            ExpandFuzzy(terms, expansion);

        string[] resolved = terms.ToArray();
        _resolved[key] = resolved;

        return resolved;
    }

    /// <summary>
    /// Every vocabulary term starting with the base (ordinal), highest document frequency first
    /// then ordinal order, capped at <see cref="RankedTextSearchEngine.MaxExpansionsPerAtom"/>.
    /// </summary>
    private void ExpandPrefix(List<string> terms, QueryExpansion expansion)
    {
        // Frequency rides with the term instead of being re-fetched by the sort's comparer: the
        // comparer would otherwise call DocumentFrequency twice per comparison, and each call is a
        // dictionary probe over the whole term — O(n log n) probes for a value that is
        // constant for a term. The order is the same either way; only the cost changes.
        var matches = new List<(string Term, int Frequency)>();

        // An index that keeps its dictionary sorted can answer the prefix as a range and decode only
        // the terms inside it; walking the whole vocabulary is the fallback.
        foreach (string term in _prefixIndex is not null
            ? _prefixIndex.TermsWithPrefix(expansion.BaseTerm)
            : EveryPrefixMatch(_vocabulary!.Vocabulary, expansion.BaseTerm))
        {
            matches.Add((term, _vocabulary!.DocumentFrequency(term)));
        }

        matches.Sort((a, b) =>
        {
            int byFrequency = b.Frequency.CompareTo(a.Frequency);
            return byFrequency != 0 ? byFrequency : string.CompareOrdinal(a.Term, b.Term);
        });

        int count = Math.Min(matches.Count, RankedTextSearchEngine.MaxExpansionsPerAtom);

        for (int i = 0; i < count; i++)
            terms.Add(matches[i].Term);
    }

    /// <summary>The vocabulary's terms starting with <paramref name="prefix"/>, for an unsorted index.</summary>
    private static IEnumerable<string> EveryPrefixMatch(IEnumerable<string> vocabulary, string prefix)
    {
        foreach (string term in vocabulary)
        {
            if (term.StartsWith(prefix, StringComparison.Ordinal))
                yield return term;
        }
    }

    /// <summary>
    /// Appends every vocabulary term within <see cref="QueryExpansion.MaxEdits"/> Levenshtein
    /// edits of the base — closest first, then highest document frequency, then ordinal —
    /// capped at <see cref="RankedTextSearchEngine.MaxExpansionsPerAtom"/>.
    /// </summary>
    private void ExpandFuzzy(List<string> terms, QueryExpansion expansion)
    {
        var candidates = new List<(string Term, int Distance)>();

        foreach (string term in _vocabulary!.Vocabulary)
        {
            // Cheap length gate before the O(m·n) distance: |len difference| ≤ budget.
            if (Math.Abs(term.Length - expansion.BaseTerm.Length) > expansion.MaxEdits)
                continue;

            int distance = LevenshteinDistance.Distance(expansion.BaseTerm, term);

            if (distance <= expansion.MaxEdits)
                candidates.Add((term, distance));
        }

        candidates.Sort((a, b) =>
        {
            int byDistance = a.Distance.CompareTo(b.Distance);
            if (byDistance != 0)
                return byDistance;

            int byFrequency = _vocabulary.DocumentFrequency(b.Term).CompareTo(_vocabulary.DocumentFrequency(a.Term));
            return byFrequency != 0 ? byFrequency : string.CompareOrdinal(a.Term, b.Term);
        });

        int count = Math.Min(candidates.Count, RankedTextSearchEngine.MaxExpansionsPerAtom);

        for (int i = 0; i < count; i++)
            terms.Add(candidates[i].Term);
    }
}
