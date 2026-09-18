namespace LexiSharp.Similarity;

/// <summary>
/// Levenshtein edit distance between two strings: the minimum number of single-character
/// insertions, deletions or substitutions to turn one into the other.
/// </summary>
/// <remarks>
/// Implemented with a rolling two-row dynamic program, so memory stays
/// <c>O(min(first.Length, second.Length))</c> regardless of input size. Uses the convention
/// that identical strings are at distance 0 and an empty string is at distance = the other
/// string's length. The distance is symmetric.
/// </remarks>
public static class LevenshteinDistance
{
    /// <summary>Computes the edit distance between the two strings (0 for identical inputs).</summary>
    public static int Distance(string first, string second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        if (first.Length == 0)
            return second.Length;

        if (second.Length == 0)
            return first.Length;

        // Keep the shorter string in the rolling row: memory O(min(m, n)).
        if (second.Length < first.Length)
            (first, second) = (second, first);

        var previous = new int[first.Length + 1];
        var current = new int[first.Length + 1];

        for (int i = 0; i <= first.Length; i++)
            previous[i] = i;

        for (int j = 1; j <= second.Length; j++)
        {
            current[0] = j;

            for (int i = 1; i <= first.Length; i++)
            {
                int substitutionCost = first[i - 1] == second[j - 1] ? 0 : 1;

                current[i] = Math.Min(
                    Math.Min(current[i - 1] + 1, previous[i] + 1),
                    previous[i - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[first.Length];
    }
}
