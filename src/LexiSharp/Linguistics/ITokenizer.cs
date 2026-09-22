namespace LexiSharp.Linguistics;

/// <summary>
/// Converts raw text into a list of normalized terms.
/// </summary>
public interface ITokenizer
{
    /// <summary>Splits and normalizes text into terms, in reading order.</summary>
    IReadOnlyList<string> Tokenize(string text);

    /// <summary>
    /// Splits and normalizes text into terms, in reading order. The default implementation
    /// materializes the span as a string and forwards to <see cref="Tokenize(string)"/>;
    /// tokenizers that can avoid the copy should override it.
    /// </summary>
    IReadOnlyList<string> Tokenize(ReadOnlySpan<char> text) => Tokenize(text.ToString());
}