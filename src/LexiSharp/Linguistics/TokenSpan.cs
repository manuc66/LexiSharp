namespace LexiSharp.Linguistics;

/// <summary>
/// A normalized term together with the half-open character range <c>[Start, Start + Length)</c>
/// it occupies in the original text. Offsets are UTF-16 indices (string indices), so slicing
/// <c>text[Start..Start + Length]</c> recovers the term's source characters.
/// </summary>
/// <param name="Term">The normalized term, exactly as <see cref="ITokenizer.Tokenize"/> produces it.</param>
/// <param name="Start">Zero-based UTF-16 index of the term's first source character.</param>
/// <param name="Length">Number of UTF-16 source characters covered by the term.</param>
public readonly record struct TokenSpan(string Term, int Start, int Length)
{
    /// <summary>Exclusive end index (<c>Start + Length</c>) of the term in the source text.</summary>
    public int End => Start + Length;
}
