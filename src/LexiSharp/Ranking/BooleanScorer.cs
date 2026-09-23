using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>How the modifiers of a <see cref="BooleanScorer"/> are combined.</summary>
public enum BooleanMatch
{
    /// <summary>A document matches only when it contains every query term (logical AND).</summary>
    AllTerms,
    /// <summary>A document matches when it contains at least one query term (logical OR).</summary>
    AnyTerm,
}

/// <summary>
/// Pure boolean filter. A matching document gets a score of <c>1</c>, a non-matching one <c>0</c>.
/// Because the <see cref="RankedTextSearchEngine"/> discards scores below <c>MinimumScore</c>,
/// this scorer behaves exactly like an exact AND/OR query while remaining model-independent.
/// </summary>
public sealed class BooleanScorer : ITermOverlapScorer, IScoreExplainer
{
    private readonly BooleanMatch _match;

    public BooleanScorer(BooleanMatch match = BooleanMatch.AllTerms)
    {
        _match = match;
    }

    /// <inheritdoc />
    public string Name => _match == BooleanMatch.AllTerms ? "Boolean (AND)" : "Boolean (OR)";

    /// <inheritdoc />
    public double Score(string documentId, IReadOnlyList<string> queryTerms, ITextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(queryTerms);

        if (queryTerms.Count == 0 || index.Count == 0)
            return 0;

        bool matches = _match == BooleanMatch.AllTerms
            ? MatchAll(documentId, queryTerms, index)
            : MatchAny(documentId, queryTerms, index);

        return matches ? 1 : 0;
    }

    private static bool MatchAll(string documentId, IReadOnlyList<string> terms, ITextIndex index)
    {
        // Per-document hot path, run once for every candidate document. Iterating by index
        // avoids the boxed enumerator a foreach over the IReadOnlyList<string> interface
        // would allocate per document.
        for (int i = 0; i < terms.Count; i++)
        {
            if (index.TermFrequency(documentId, terms[i]) == 0)
                return false;
        }

        return true;
    }

    private static bool MatchAny(string documentId, IReadOnlyList<string> terms, ITextIndex index)
    {
        // Same reasoning as MatchAll: keep the allocation-free indexed loop.
        for (int i = 0; i < terms.Count; i++)
        {
            if (index.TermFrequency(documentId, terms[i]) > 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Explains a boolean score: <c>1</c> when the document matches, <c>0</c> otherwise. A
    /// boolean score has no natural additive split, so the unit score is <b>spread evenly</b>
    /// over the matched terms — the contributions sum back to <see cref="ScoreExplanation.TotalScore"/>
    /// while still listing which terms matched, with their frequencies.
    /// </summary>
    /// <param name="documentId">Id of the document to explain.</param>
    /// <param name="queryTerms">Terms of the query, already tokenized and distinct.</param>
    /// <param name="index">The shared index holding corpus statistics.</param>
    public ScoreExplanation Explain(string documentId, IReadOnlyList<string> queryTerms, ITextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(queryTerms);

        int documentCount = index.Count;
        int documentLength = index.DocumentLength(documentId);
        double averageLength = index.AverageDocumentLength;
        double lengthRatio = averageLength > 0 ? documentLength / averageLength : 0;

        // TermDeduplicator.Distinct returns the input unchanged when it is already distinct,
        // so this stays allocation-free on the engine's usual pre-deduplicated query.
        var terms = TermDeduplicator.Distinct(queryTerms);

        var matched = new List<string>();

        if (documentCount > 0 && documentLength > 0)
        {
            for (int i = 0; i < terms.Count; i++)
            {
                if (index.TermFrequency(documentId, terms[i]) > 0)
                    matched.Add(terms[i]);
            }
        }

        bool matches = terms.Count > 0 && (_match == BooleanMatch.AllTerms
            ? matched.Count == terms.Count
            : matched.Count > 0);

        var contributions = new List<TermContribution>();

        if (matches)
        {
            double share = 1.0 / matched.Count;

            foreach (var term in matched)
                contributions.Add(new TermContribution(
                    term,
                    index.TermFrequency(documentId, term),
                    index.DocumentFrequency(term),
                    0,
                    share));
        }

        var parameters = new Dictionary<string, double>
        {
            ["allTerms"] = _match == BooleanMatch.AllTerms ? 1 : 0,
        };

        return new ScoreExplanation(
            documentId,
            Name,
            matches ? 1 : 0,
            documentLength,
            averageLength,
            lengthRatio,
            1.0,
            contributions,
            parameters);
    }
}