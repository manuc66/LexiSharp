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
public sealed class BooleanScorer : ITextScorer, ITermOverlapScorer
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

        return _match == BooleanMatch.AllTerms
            ? MatchAll(documentId, queryTerms, index) ? 1 : 0
            : MatchAny(documentId, queryTerms, index) ? 1 : 0;
    }

    private static bool MatchAll(string documentId, IReadOnlyList<string> terms, ITextIndex index)
    {
        foreach (var term in terms)
        {
            if (index.TermFrequency(documentId, term) == 0)
                return false;
        }

        return true;
    }

    private static bool MatchAny(string documentId, IReadOnlyList<string> terms, ITextIndex index)
    {
        foreach (var term in terms)
        {
            if (index.TermFrequency(documentId, term) > 0)
                return true;
        }

        return false;
    }
}