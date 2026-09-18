namespace LexiSharp.Linguistics;

/// <summary>
/// Converts raw text into a list of normalized terms.
/// </summary>
public interface ITokenizer
{
    /// <summary>Splits and normalizes text into terms, in reading order.</summary>
    IReadOnlyList<string> Tokenize(string text);
}