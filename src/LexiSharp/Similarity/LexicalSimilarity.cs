using System.Collections.Frozen;
using LexiSharp.Linguistics;

namespace LexiSharp.Similarity;

/// <summary>
/// Pairwise lexical similarity measures over short strings — the in-memory answer to "are these
/// two texts roughly the same?": near-duplicate detection, autocomplete-style suggestions,
/// record de-duplication, spell-check shortlists.
/// </summary>
/// <remarks>
/// Two families of measures:
/// <list type="bullet">
/// <item><description>
/// <see cref="Jaccard"/> and <see cref="SorensenDice"/> compare <b>token sets</b> produced by a
/// <see cref="ITokenizer"/> (same normalization as the rest of the library: case-folding,
/// diacritics stripped), so they measure overlap at word level.
/// </description></item>
/// <item><description>
/// <see cref="Trigram"/> compares <b>character trigram sets</b> with two-space word padding —
/// the same shape of signal a trigram index (e.g. <c>pg_trgm</c>) relies on, but computed on
/// demand with no index at all.
/// </description></item>
/// </list>
/// All measures return a similarity in <c>[0, 1]</c> and treat empty inputs symmetrically
/// (two empty texts are perfectly similar; one empty, one not, score 0). These functions are
/// pairwise helpers, not an index: scoring a corpus against a query is what the
/// <see cref="Ranking"/> scorers are for.
/// </remarks>
public static class LexicalSimilarity
{
    /// <summary>
    /// Jaccard similarity of the two token sets: <c>|A ∩ B| / |A ∪ B|</c>, symmetric and
    /// indifferent to word order and repetition.
    /// </summary>
    public static double Jaccard(string first, string second, ITokenizer? tokenizer = null)
    {
        (var firstSet, var secondSet) = TokenSets(first, second, tokenizer);

        if (firstSet.Count == 0 && secondSet.Count == 0)
            return 1;

        if (firstSet.Count == 0 || secondSet.Count == 0)
            return 0;

        return firstSet.Count(secondSet.Contains) / (double)(firstSet.Count + secondSet.Count - firstSet.Count(secondSet.Contains));
    }

    /// <summary>
    /// Sørensen–Dice coefficient of the two token sets: <c>2·|A ∩ B| / (|A| + |B|)</c> — like
    /// Jaccard but more forgiving of small overlaps on short texts.
    /// </summary>
    public static double SorensenDice(string first, string second, ITokenizer? tokenizer = null)
    {
        (var firstSet, var secondSet) = TokenSets(first, second, tokenizer);

        if (firstSet.Count == 0 && secondSet.Count == 0)
            return 1;

        int intersection = firstSet.Count(secondSet.Contains);

        return 2.0 * intersection / (firstSet.Count + secondSet.Count);
    }

    /// <summary>
    /// Similarity of the two character trigram sets: <c>2·|A ∩ B| / (|A| + |B|)</c>, with both
    /// texts padded by two spaces on each side (the <c>pg_trgm</c> convention) so word
    /// boundaries contribute signal too.
    /// </summary>
    public static double Trigram(string first, string second)
    {
        var firstSet = TrigramSet(first);
        var secondSet = TrigramSet(second);

        if (firstSet.Count == 0 && secondSet.Count == 0)
            return 1;

        int intersection = firstSet.Count(secondSet.Contains);

        int total = firstSet.Count + secondSet.Count;

        return total == 0 ? 1 : 2.0 * intersection / total;
    }

    private static (FrozenSet<string> First, FrozenSet<string> Second) TokenSets(
        string first, string second, ITokenizer? tokenizer)
    {
        tokenizer ??= Tokenizer.Default;

        var firstTokens = tokenizer.Tokenize(first);
        var secondTokens = tokenizer.Tokenize(second);

        var firstSet = firstTokens.Count == 0
            ? EmptySet
            : firstTokens.ToFrozenSet(StringComparer.Ordinal);
        var secondSet = secondTokens.Count == 0
            ? EmptySet
            : secondTokens.ToFrozenSet(StringComparer.Ordinal);

        return (firstSet, secondSet);
    }

    private static readonly FrozenSet<string> EmptySet = FrozenSet<string>.Empty;

    private static FrozenSet<string> TrigramSet(string text)
    {
        var normalized = Tokenizer.Normalize(text);

        if (normalized.Length == 0)
            return EmptySet;

        var padded = $"  {normalized}  ";

        var builder = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i + 3 <= padded.Length; i++)
            builder.Add(padded.Substring(i, 3));

        return builder.ToFrozenSet(StringComparer.Ordinal);
    }
}
