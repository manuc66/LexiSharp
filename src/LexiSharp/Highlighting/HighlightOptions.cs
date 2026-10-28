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

    /// <summary>
    /// Whether the document's own characters are HTML-encoded before they are tagged. Default:
    /// <c>false</c>, which returns the source text verbatim between the tags.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The output of <see cref="TextHighlighter.HighlightFull"/> and
    /// <see cref="TextHighlighter.Highlight"/> is <b>markup</b>: <see cref="PreTag"/> and
    /// <see cref="PostTag"/> are inserted into the document's text, and the text between them is
    /// copied exactly. That is the right contract for a caller writing to a console, a terminal or a
    /// desktop control, and the wrong one for a caller writing to a browser page: a document
    /// containing <c>&lt;img src=x onerror=...&gt;</c> reaches the page as an element, and an
    /// ampersand is read as the start of an entity.
    /// </para>
    /// <para>
    /// With this set, only the document's characters are encoded (the tags are the caller's and
    /// stay as written), so the result can be inserted into an HTML element's content as-is. It is
    /// off by default because the existing output is byte-for-byte pinned and because encoding is a
    /// decision about a context the highlighter cannot see — a caller sending the page as JSON to a
    /// client that renders plain text must not have its text encoded.
    /// </para>
    /// <para>
    /// This encodes element content, not attribute values or URLs: a tag the caller supplies, or a
    /// document used as an attribute value, is still the caller's to escape.
    /// </para>
    /// </remarks>
    public bool EscapeHtml { get; init; }
}
