namespace LexiSharp.Linguistics;

/// <summary>
/// A tokenizer that can also report where each normalized term sits in the source text —
/// the basis for search-result highlighting.
/// </summary>
public interface ISpanTokenizer : ITokenizer
{
    /// <summary>
    /// Splits and normalizes text into terms paired with their <c>[Start, Start + Length)</c>
    /// position in <paramref name="text"/>, in reading order. The terms are exactly
    /// <see cref="ITokenizer.Tokenize(string)"/>'s output for the same text and configuration;
    /// n-gram spans cover the whole source region of the phrase (separators included).
    /// </summary>
    IReadOnlyList<TokenSpan> TokenizeWithSpans(string text);

    /// <summary>
    /// Splits and normalizes text into terms paired with their <c>[Start, Start + Length)</c>
    /// position in <paramref name="text"/>, in reading order. The default implementation
    /// materializes the span as a string and forwards to <see cref="TokenizeWithSpans(string)"/>;
    /// tokenizers that can avoid the copy should override it.
    /// </summary>
    IReadOnlyList<TokenSpan> TokenizeWithSpans(ReadOnlySpan<char> text) => TokenizeWithSpans(text.ToString());
}
