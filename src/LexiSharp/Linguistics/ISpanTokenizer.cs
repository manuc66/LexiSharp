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

    /// <summary>
    /// Splits and normalizes <paramref name="text"/> into <paramref name="destination"/>, in reading
    /// order, without materializing a string for a term whose normalized form the source already
    /// holds. Returns how many terms were written.
    /// </summary>
    /// <returns>
    /// The number of terms, or <c>-1</c> when the call cannot be served as spans — a destination too
    /// small for the terms, or an analysis whose terms are not the source slices (an n-gram pass, a
    /// stop-word list, a stemmer, a possessive trim, a joining segmentation). A caller that receives
    /// <c>-1</c> falls back to <see cref="ITokenizer.Tokenize(ReadOnlySpan{char})"/>, so the default
    /// implementation returns <c>-1</c> and a tokenizer that cannot serve it need not override it.
    /// </returns>
    /// <param name="text">The text to tokenize.</param>
    /// <param name="destination">Where the terms go; the caller sizes it.</param>
    int TokenizeNormalized(ReadOnlySpan<char> text, Span<NormalizedTerm> destination) => -1;
}
