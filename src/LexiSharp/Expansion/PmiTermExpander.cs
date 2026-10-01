using System;
using System.Collections.Generic;
using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Expansion;

/// <summary>
/// A corpus-derived <see cref="ITermExpander"/>: it learns which terms habitually appear in
/// the same context window and expands a document with the top positively associated terms,
/// each carrying a weak <see cref="ExpandedTerm.Weight"/>.
/// </summary>
/// <remarks>
/// <para>
/// Association is measured with positive pointwise mutual information (PPMI) between terms
/// over a sliding window of tokens: a pair that appears together more often than chance gets a
/// positive PPMI and survives as a candidate. Among the candidates of an input term, ranking
/// is by raw co-occurrence count — the classic document/query expansion bias — so a term that
/// reliably travels with the input outranks a rare one-off companion. The exposed
/// <see cref="ExpandedTerm.Weight"/> is the neighbor's co-occurrence count normalized by the
/// input term's strongest candidate (in <c>(0, 1]</c>). This is the purely statistical core of
/// the « semantic lexical index » — embedding-free, deterministic, offline, and independent of
/// any neural model, which can still be substituted through <see cref="ITermExpander"/>.
/// </para>
/// <para>
/// The pair-count table is kept in memory; on very large corpora this is the memory driver of
/// the learning pass. The learned model is intentionally small afterwards: only each term's
/// positive-PMI neighbors survive past <see cref="LearnFrom"/>.
/// </para>
/// </remarks>
public sealed class PmiTermExpander : ITermExpander
{
    private readonly PmiTermExpanderOptions _options;
    private readonly Dictionary<string, List<Association>> _associations;

    private PmiTermExpander(
        PmiTermExpanderOptions options,
        Dictionary<string, List<Association>> associations)
    {
        _options = options;
        _associations = associations;
    }

    /// <summary>
    /// Learns the term–term association model from a corpus. The tokens of each document are
    /// counted in a sliding context window; tokenization uses
    /// <paramref name="tokenizer"/> (default: the stock <see cref="Tokenizer.Default"/>).
    /// </summary>
    /// <param name="corpus">The documents to analyze. Does not retain the corpus itself.</param>
    /// <param name="tokenizer">Optional custom tokenizer, mirroring the one used at indexing.</param>
    /// <param name="options">Optional tuning knobs.</param>
    /// <returns>A ready-to-use expander over the learned associations.</returns>
    public static PmiTermExpander LearnFrom(
        IEnumerable<SearchDocument> corpus,
        ITokenizer? tokenizer = null,
        PmiTermExpanderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(corpus);

        var opts = options ?? new PmiTermExpanderOptions();
        var tok = tokenizer ?? Tokenizer.Default;

        var pairCounts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var windowCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        long totalWindows = 0;

        foreach (var document in corpus)
        {
            var tokens = tok.Tokenize(document.Text);
            int windowCount = tokens.Count - opts.ContextWindowSize + 1;
            if (windowCount <= 0)
                continue;

            totalWindows += windowCount;

            for (int start = 0; start < windowCount; start++)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = start; i < start + opts.ContextWindowSize; i++)
                    seen.Add(tokens[i]);

                foreach (var term in seen)
                {
                    windowCounts.TryGetValue(term, out int count);
                    windowCounts[term] = count + 1;
                }

                foreach (var a in seen)
                {
                    foreach (var b in seen)
                    {
                        if (string.CompareOrdinal(a, b) >= 0)
                            continue;

                        if (!pairCounts.TryGetValue(a, out var withB))
                        {
                            withB = new Dictionary<string, int>(StringComparer.Ordinal);
                            pairCounts[a] = withB;
                        }

                        withB.TryGetValue(b, out int pairCount);
                        withB[b] = pairCount + 1;
                    }
                }
            }
        }

        var associations = new Dictionary<string, List<Association>>(StringComparer.Ordinal);

        foreach (var (a, withB) in pairCounts)
        foreach (var (b, cAb) in withB)
        {
            if (cAb < opts.MinimumCoOccurrence)
                continue;

            if (!windowCounts.TryGetValue(a, out int windowCountA) ||
                windowCountA < opts.MinimumNeighborWindowFrequency ||
                windowCountA > opts.MaxWindowDensity * totalWindows ||
                !windowCounts.TryGetValue(b, out int windowCountB) ||
                windowCountB < opts.MinimumNeighborWindowFrequency ||
                windowCountB > opts.MaxWindowDensity * totalWindows)
                continue;

            double pmi = Math.Log(((double)cAb * totalWindows) / ((double)windowCountA * windowCountB));
            if (pmi <= opts.MinimumPmi)
                continue;

            AddAssociation(associations, a, b, cAb, pmi);
            AddAssociation(associations, b, a, cAb, pmi);
        }

        foreach (var list in associations.Values)
        {
            list.Sort((x, y) =>
            {
                int byCount = y.Count.CompareTo(x.Count);
                if (byCount != 0)
                    return byCount;

                int byPmi = y.Pmi.CompareTo(x.Pmi);
                if (byPmi != 0)
                    return byPmi;

                // Total order, or the sort is not one. Count and Pmi leave genuine ties -- two terms
                // can share both -- and List<T>.Sort is unstable, so without a final key the order
                // of the tied entries is whatever their insertion order happened to be. That
                // insertion order comes from the dictionary walk above, hence from the corpus
                // enumeration order, hence from the filesystem. The neighbours are then summed into
                // a document score, so an unstable order here perturbs the last bits of that sum and
                // reorders documents whose scores differ only by floating-point noise.
                return string.CompareOrdinal(x.Neighbor, y.Neighbor);
            });

            // The maximum over the list, not over the first entry. Under count ranking those are the
            // same entry, and reading list[0] was a shortcut; under association ranking the
            // best-associated neighbour is not the most frequent one, so normalising by it produced
            // weights above 1 for a value documented as being in (0, 1].
            int maxCount = 0;

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Count > maxCount)
                    maxCount = list[i].Count;
            }

            for (int i = 0; i < list.Count; i++)
            {
                var entry = list[i];
                list[i] = new Association(entry.Neighbor, entry.Count, entry.Pmi, (double)entry.Count / maxCount);
            }
        }

        return new PmiTermExpander(opts, associations);
    }

    /// <summary>
    /// Returns the top positively associated terms of the given document terms — never echoing
    /// an input term, each input term contributing at most
    /// <see cref="PmiTermExpanderOptions.MaxTermsPerInputTerm"/> neighbors, and the whole set
    /// capped at <see cref="PmiTermExpanderOptions.MaxTotalTerms"/>.
    /// </summary>
    public IReadOnlyCollection<ExpandedTerm> Expand(IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);

        var result = new List<ExpandedTerm>();
        var inputs = new HashSet<string>(terms, StringComparer.Ordinal);
        var chosen = new HashSet<string>(inputs, StringComparer.Ordinal);
        var processed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var term in terms)
        {
            if (!processed.Add(term))
                continue;

            if (!_associations.TryGetValue(term, out var neighbors))
                continue;

            int taken = 0;
            foreach (var association in neighbors)
            {
                if (chosen.Contains(association.Neighbor))
                    continue;

                chosen.Add(association.Neighbor);
                result.Add(new ExpandedTerm(association.Neighbor, association.Weight));

                if (++taken >= _options.MaxTermsPerInputTerm)
                    break;
            }

            if (result.Count >= _options.MaxTotalTerms)
                break;
        }

        return result;
    }

    private static void AddAssociation(
        Dictionary<string, List<Association>> associations,
        string from,
        string to,
        int count,
        double pmi)
    {
        if (!associations.TryGetValue(from, out var list))
        {
            list = new List<Association>();
            associations[from] = list;
        }

        // The association carries its mutual information. It used not to: the PPMI was computed
        // here, used as a filter a few lines up, and then dropped on the floor, leaving every
        // Association.Pmi at its default of 0. That made the tiebreak the sort documentation
        // describes compare 0 against 0 and never fire, and made the whole statistic unrankable.
        list.Add(new Association(to, count, pmi));
    }

    private readonly record struct Association(string Neighbor, int Count, double Pmi = 0, double Weight = 0);
}