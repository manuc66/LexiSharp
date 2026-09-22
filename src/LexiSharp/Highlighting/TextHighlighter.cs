using System.Text;
using LexiSharp.Linguistics;

namespace LexiSharp.Highlighting;

/// <summary>
/// Wraps query matches in the original text: full-string marking for short fields (titles,
/// labels) or padded, word-snapped snippets for long documents.
/// </summary>
/// <remarks>
/// Matching runs through <see cref="ISpanTokenizer.TokenizeWithSpans(string)"/> on the document, so
/// hits land on token boundaries and share the library's normalization (case, accents, stop
/// words, stemming, n-grams). Query terms are compared ordinally against the document's
/// normalized terms — tokenize the query with the same tokenizer first (e.g.
/// <c>QueryParser.Parse(query, tokenizer).AllTerms</c>). A query term with no matching
/// document token simply contributes no range.
/// <para>
/// Overlapping ranges (n-gram tokenizers) are merged before tagging, so the output never
/// nests or interleaves tags.
/// </para>
/// </remarks>
public static class TextHighlighter
{
    /// <summary>
    /// Returns the merged source ranges <c>[Start, End)</c> of every query term occurrence in
    /// <paramref name="text"/>, in reading order. Empty when no term matches.
    /// </summary>
    public static IReadOnlyList<Range> MatchRanges(
        string text,
        IReadOnlyList<string> queryTerms,
        ISpanTokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(queryTerms);
        ArgumentNullException.ThrowIfNull(tokenizer);

        if (queryTerms.Count == 0)
            return Array.Empty<Range>();

        var wanted = new HashSet<string>(queryTerms, StringComparer.Ordinal);
        var spans = tokenizer.TokenizeWithSpans(text);
        List<Range>? ranges = null;

        for (int i = 0; i < spans.Count; i++)
        {
            var span = spans[i];

            if (!wanted.Contains(span.Term))
                continue;

            (ranges ??= new List<Range>()).Add(new Range(span.Start, span.End));
        }

        if (ranges is null)
            return Array.Empty<Range>();

        return Merge(ranges);
    }

    /// <summary>
    /// Returns <paramref name="text"/> with every query-term occurrence wrapped in
    /// <see cref="HighlightOptions.PreTag"/>/<see cref="HighlightOptions.PostTag"/>; the text
    /// itself is returned unchanged when nothing matches.
    /// </summary>
    public static string HighlightFull(
        string text,
        IReadOnlyList<string> queryTerms,
        ISpanTokenizer tokenizer,
        HighlightOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        options ??= HighlightOptions.Default;

        var ranges = MatchRanges(text, queryTerms, tokenizer);

        if (ranges.Count == 0)
            return text;

        return InsertTags(text, 0, text.Length, ranges, options);
    }

    /// <summary>
    /// Returns up to <see cref="HighlightOptions.MaxSnippets"/> excerpts of <paramref name="text"/>,
    /// each covering a cluster of nearby matches with
    /// <see cref="HighlightOptions.Padding"/> characters of context snapped outward to word
    /// boundaries. Empty when nothing matches or <see cref="HighlightOptions.MaxSnippets"/>
    /// is 0 or less.
    /// </summary>
    public static IReadOnlyList<HighlightSnippet> Highlight(
        string text,
        IReadOnlyList<string> queryTerms,
        ISpanTokenizer tokenizer,
        HighlightOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        options ??= HighlightOptions.Default;

        if (options.MaxSnippets <= 0)
            return Array.Empty<HighlightSnippet>();

        var ranges = MatchRanges(text, queryTerms, tokenizer);

        if (ranges.Count == 0)
            return Array.Empty<HighlightSnippet>();

        int padding = Math.Max(0, options.Padding);
        var windows = ClusterIntoWindows(text, ranges, padding);

        var snippets = new List<HighlightSnippet>(Math.Min(windows.Count, options.MaxSnippets));

        for (int i = 0; i < windows.Count && snippets.Count < options.MaxSnippets; i++)
        {
            var (start, end) = windows[i];
            snippets.Add(new HighlightSnippet(start, end - start, InsertTags(text, start, end, ranges, options)));
        }

        return snippets;
    }

    /// <summary>
    /// Groups ranges whose gap is at most <paramref name="padding"/> × 2 into one window per
    /// cluster, pads each cluster, snaps the window outward to word boundaries and merges
    /// windows that now overlap.
    /// </summary>
    private static List<(int Start, int End)> ClusterIntoWindows(string text, IReadOnlyList<Range> ranges, int padding)
    {
        var clusters = new List<(int Start, int End)>();
        int clusterStart = ranges[0].Start.Value;
        int clusterEnd = ranges[0].End.Value;

        for (int i = 1; i < ranges.Count; i++)
        {
            int start = ranges[i].Start.Value;
            int end = ranges[i].End.Value;

            // ranges are sorted and non-overlapping after Merge: start - clusterEnd is the gap.
            if (start - clusterEnd <= 2 * padding)
            {
                if (end > clusterEnd)
                    clusterEnd = end;
            }
            else
            {
                clusters.Add((clusterStart, clusterEnd));
                clusterStart = start;
                clusterEnd = end;
            }
        }

        clusters.Add((clusterStart, clusterEnd));

        var windows = new List<(int Start, int End)>(clusters.Count);

        foreach (var (start, end) in clusters)
        {
            int paddedStart = SnapStart(text, start - padding);
            int paddedEnd = SnapEnd(text, end + padding);

            // Snapping outward can make neighbouring windows touch or overlap — keep one window.
            if (windows.Count > 0 && paddedStart <= windows[^1].End)
            {
                var previous = windows[^1];
                windows[^1] = (previous.Start, Math.Max(previous.End, paddedEnd));
            }
            else
            {
                windows.Add((paddedStart, paddedEnd));
            }
        }

        return windows;
    }

    /// <summary>Moves <paramref name="index"/> left to the start of the word it falls inside.</summary>
    private static int SnapStart(string text, int index)
    {
        int i = Math.Clamp(index, 0, text.Length);

        while (i > 0 && !char.IsWhiteSpace(text[i - 1]))
            i--;

        return i;
    }

    /// <summary>Moves <paramref name="index"/> right to the end of the word it falls inside.</summary>
    private static int SnapEnd(string text, int index)
    {
        int i = Math.Clamp(index, 0, text.Length);

        while (i < text.Length && !char.IsWhiteSpace(text[i]))
            i++;

        return i;
    }

    /// <summary>Merges overlapping (or touching) sorted ranges — n-gram tokenizers overlap.</summary>
    private static List<Range> Merge(List<Range> ranges)
    {
        var merged = new List<Range>(ranges.Count);
        var current = ranges[0];

        for (int i = 1; i < ranges.Count; i++)
        {
            var next = ranges[i];

            if (next.Start.Value <= current.End.Value)
            {
                if (next.End.Value > current.End.Value)
                    current = new Range(current.Start, next.End);
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }

        merged.Add(current);
        return merged;
    }

    /// <summary>
    /// Renders <c>text[from..to)</c> with tags around every range intersection; ranges are
    /// already merged, so tags never nest.
    /// </summary>
    private static string InsertTags(
        string text,
        int from,
        int to,
        IReadOnlyList<Range> ranges,
        HighlightOptions options)
    {
        var builder = new StringBuilder(to - from + 16);
        int position = from;

        for (int i = 0; i < ranges.Count; i++)
        {
            int start = ranges[i].Start.Value;
            int end = ranges[i].End.Value;

            if (end <= from)
                continue;

            if (start >= to)
                break;

            int tagStart = Math.Max(start, from);
            int tagEnd = Math.Min(end, to);

            if (tagEnd <= position)
                continue;

            if (tagStart < position)
                tagStart = position;

            builder.Append(text, position, tagStart - position);
            builder.Append(options.PreTag);
            builder.Append(text, tagStart, tagEnd - tagStart);
            builder.Append(options.PostTag);
            position = tagEnd;
        }

        builder.Append(text, position, to - position);
        return builder.ToString();
    }
}
