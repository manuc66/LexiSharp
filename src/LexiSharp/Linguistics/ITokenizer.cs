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

    /// <summary>
    /// Splits and normalizes <paramref name="text"/>, appending the terms to
    /// <paramref name="destination"/>, which the caller owns and may size for the call. The default
    /// implementation forwards to <see cref="Tokenize(ReadOnlySpan{char})"/> and adds what it
    /// returns; a tokenizer that can fill a list directly should override it, which is what keeps a
    /// caller from paying for a list sized for a document on a call that tokenizes a query.
    /// </summary>
    void TokenizeInto(ReadOnlySpan<char> text, List<string> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        foreach (string term in Tokenize(text))
            destination.Add(term);
    }
}