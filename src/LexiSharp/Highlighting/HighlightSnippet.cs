namespace LexiSharp.Highlighting;

/// <summary>
/// One highlighted excerpt: the source window <c>[Start, Start + Length)</c> with match tags
/// already injected into <see cref="Text"/>.
/// </summary>
/// <param name="Start">Zero-based UTF-16 index of the window start in the source text.</param>
/// <param name="Length">Window length in UTF-16 characters.</param>
/// <param name="Text">
/// The excerpt with <see cref="HighlightOptions.PreTag"/>/<see cref="HighlightOptions.PostTag"/>
/// around each match.
/// </param>
public sealed record HighlightSnippet(int Start, int Length, string Text)
{
    /// <summary>Exclusive end index of the window in the source text.</summary>
    public int End => Start + Length;
}
