namespace LexiSharp.Highlighting;

/// <summary>Knobs for <see cref="TextHighlighter"/>.</summary>
public sealed record HighlightOptions
{
    /// <summary>Default options: <c>&lt;em&gt;</c> tags, up to 3 snippets, 40 characters of padding.</summary>
    public static HighlightOptions Default { get; } = new();

    /// <summary>Opening tag wrapped around each match. Default: <c>&lt;em&gt;</c>.</summary>
    public string PreTag { get; init; } = "<em>";

    /// <summary>Closing tag wrapped around each match. Default: <c>&lt;/em&gt;</c>.</summary>
    public string PostTag { get; init; } = "</em>";

    /// <summary>
    /// Maximum number of snippets returned by <see cref="TextHighlighter.Highlight"/>;
    /// <c>0</c> or less disables snippets. Default: <c>3</c>.
    /// </summary>
    public int MaxSnippets { get; init; } = 3;

    /// <summary>
    /// Characters of context kept before and after each match cluster; snippets snap outward
    /// to word boundaries so a match is never cut mid-word. Negative values clamp to 0.
    /// Default: <c>40</c>.
    /// </summary>
    public int Padding { get; init; } = 40;
}
