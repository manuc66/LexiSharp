using System.Text;

namespace LexiSharp.Similarity;

/// <summary>
/// Levenshtein edit distance between two strings: the minimum number of single-character
/// insertions, deletions or substitutions to turn one into the other.
/// </summary>
/// <remarks>
/// Uses Myers' bit-parallel algorithm when the longer input fits a single 64-bit word and both
/// inputs are ASCII — the common case for normalized terms and short strings: the distance is
/// computed in <c>O(|shorter|)</c> machine-word operations instead of the <c>O(|a|·|b|)</c>
/// dynamic program, with no per-call allocation (a per-thread character-mask table). Non-ASCII
/// inputs, and inputs longer than 64 chars, fall back to a rolling two-row dynamic program, so
/// memory stays <c>O(min(|a|, |b|))</c> regardless of input size. Uses the convention that
/// identical strings are at distance 0 and an empty string is at distance = the other string's
/// length. The distance is symmetric.
/// </remarks>
public static class LevenshteinDistance
{
    // Myers packs the pattern into one 64-bit word, and the mask table is indexed by character:
    // ASCII only, so a 128-entry per-thread table is enough (1 KiB).
    private const int AsciiLimit = 128;

    [ThreadStatic]
    private static ulong[]? _peqScratch;

    /// <summary>Computes the edit distance between the two strings (0 for identical inputs).</summary>
    public static int Distance(string first, string second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        if (first.Length == 0)
            return second.Length;

        if (second.Length == 0)
            return first.Length;

        // Myers' bit-parallel algorithm packs the longer string into one 64-bit word and
        // consumes the shorter string one character per iteration. Both must fit the word,
        // and both must be ASCII to be indexable by the 128-entry mask table.
        if (first.Length <= 64 && second.Length <= 64)
        {
            string pattern = first.Length >= second.Length ? first : second;
            string text = first.Length >= second.Length ? second : first;

            if (Ascii.IsValid(pattern) && Ascii.IsValid(text))
                return Distance64(pattern, text);
        }

        return ScalarDistance(first, second);
    }

    /// <summary>
    /// Myers' bit-vector algorithm (Myers, 1999) for an ASCII <paramref name="pattern"/> up to
    /// 64 chars; <paramref name="text"/> is the shorter input and drives the iteration count.
    /// </summary>
    private static int Distance64(string pattern, string text)
    {
        // Per-thread scratch, left clean after each call: the ASCII fast path allocates
        // nothing once warmed up.
        ulong[] peq = _peqScratch ??= new ulong[AsciiLimit];

        int m = pattern.Length;

        for (int i = 0; i < m; i++)
            peq[pattern[i]] |= 1UL << i;

        ulong pv = ulong.MaxValue;
        ulong mv = 0UL;
        ulong last = 1UL << (m - 1);
        int score = m;

        for (int j = 0; j < text.Length; j++)
        {
            ulong eq = peq[text[j]];

            ulong xv = eq | mv;
            ulong xh = (((eq & pv) + pv) ^ pv) | eq;

            ulong ph = mv | ~(xh | pv);
            ulong mh = pv & xh;

            if ((ph & last) != 0)
                score++;
            if ((mh & last) != 0)
                score--;

            ph = (ph << 1) | 1UL;
            mh <<= 1;

            pv = mh | ~(xv | ph);
            mv = ph & xv;
        }

        // Restore the invariant: only the pattern's characters were touched.
        for (int i = 0; i < m; i++)
            peq[pattern[i]] = 0;

        return score;
    }

    /// <summary>
    /// Rolling two-row dynamic program (memory <c>O(min(|first|, |second|))</c>), used for
    /// inputs too long or too non-ASCII for the bit-parallel fast path.
    /// </summary>
    private static int ScalarDistance(string first, string second)
    {
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
