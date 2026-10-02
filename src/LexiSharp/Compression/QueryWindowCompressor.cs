using System.Text;
using LexiSharp.Core;

namespace LexiSharp.Compression;

/// <summary>
/// The model-free <see cref="IContextCompressor"/>: keeps the windows of words around each
/// occurrence of a query term and drops everything else — the deterministic, offline cousin of
/// a learned compressor, and the case that makes the seam testable without a model.
/// </summary>
/// <remarks>
/// <para>
/// Each occurrence of every query term opens a window of <c>windowRadius</c> words on
/// either side; adjacent or overlapping windows merge into one passage; passages join with an
/// ellipsis separator. A document whose text contains none of the query terms compresses to
/// nothing — there is no query-relevant window to keep.
/// </para>
/// <para>
/// Matching is literal and case-insensitive against the surface words: a stemmed query term
/// matches its exact surface form only, and punctuation-attached occurrences count as distinct
/// words. The trade is stated rather than hidden — this compressor is about proximity, not
/// morphology.
/// </para>
/// </remarks>
public sealed class QueryWindowCompressor : IContextCompressor
{
    private const string Ellipsis = " … ";

    private readonly int _windowRadius;

    /// <param name="windowRadius">Words kept on each side of every query-term occurrence.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="windowRadius"/> is not positive.</exception>
    public QueryWindowCompressor(int windowRadius = 16)
    {
        if (windowRadius < 1)
            throw new ArgumentOutOfRangeException(nameof(windowRadius), windowRadius, "windowRadius must be at least 1.");

        _windowRadius = windowRadius;
    }

    /// <inheritdoc />
    public string Name => $"query-window({_windowRadius})";

    /// <inheritdoc />
    public string Compress(string documentText, IReadOnlyList<string> queryTerms)
    {
        ArgumentNullException.ThrowIfNull(documentText);
        ArgumentNullException.ThrowIfNull(queryTerms);

        if (queryTerms.Count == 0)
            return string.Empty;

        var words = documentText.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0)
            return string.Empty;

        // One window per query-term occurrence, then merge the overlapping ones.
        var windows = CollectWindows(words, queryTerms);

        if (windows.Count == 0)
            return string.Empty;

        windows.Sort(static (left, right) =>
        {
            int byStart = left.Start.CompareTo(right.Start);
            return byStart != 0 ? byStart : right.End.CompareTo(left.End);
        });

        return Join(Reduce(windows), words);
    }

    private List<(int Start, int End)> CollectWindows(string[] words, IReadOnlyList<string> queryTerms)
    {
        var windows = new List<(int Start, int End)>();

        for (int i = 0; i < words.Length; i++)
        {
            if (!IsQueryTerm(words[i], queryTerms))
                continue;

            windows.Add((
                Math.Max(0, i - _windowRadius),
                Math.Min(words.Length, i + _windowRadius + 1)));
        }

        return windows;
    }

    private static bool IsQueryTerm(string word, IReadOnlyList<string> queryTerms)
    {
        for (int t = 0; t < queryTerms.Count; t++)
        {
            if (string.Equals(word, queryTerms[t], StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Merges overlapping or adjacent windows, in sort order, into disjoint passages.</summary>
    private static List<(int Start, int End)> Reduce(List<(int Start, int End)> windows)
    {
        var merged = new List<(int Start, int End)>();

        foreach (var window in windows)
        {
            if (merged.Count > 0 && window.Start <= merged[^1].End)
            {
                var last = merged[^1];
                merged[^1] = (last.Start, Math.Max(last.End, window.End));
            }
            else
            {
                merged.Add(window);
            }
        }

        return merged;
    }

    private static string Join(List<(int Start, int End)> passages, string[] words)
    {
        var builder = new StringBuilder();

        for (int p = 0; p < passages.Count; p++)
        {
            if (p > 0)
                builder.Append(Ellipsis);

            var passage = passages[p];

            for (int i = passage.Start; i < passage.End; i++)
            {
                if (i > passage.Start)
                    builder.Append(' ');

                builder.Append(words[i]);
            }
        }

        return builder.ToString();
    }
}