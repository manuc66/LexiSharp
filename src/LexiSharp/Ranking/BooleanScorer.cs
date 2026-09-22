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
public sealed class BooleanScorer : ITermOverlapScorer
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
        // Per-document hot path: a LINQ All() here allocates an enumerator for every scored
        // document, so the short-circuit loop is deliberate.
        foreach (var term in terms) // NOSONAR:S3267
        {
            if (index.TermFrequency(documentId, term) == 0)
                return false;
        }

        return true;
    }

    private static bool MatchAny(string documentId, IReadOnlyList<string> terms, ITextIndex index)
    {
        // Same reasoning as MatchAll: keep the allocation-free loop.
        foreach (var term in terms) // NOSONAR:S3267
        {
            if (index.TermFrequency(documentId, term) > 0)
                return true;
        }

        return false;
    }
}