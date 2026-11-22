using System.Globalization;

namespace LexiSharp.Linguistics;

/// <summary>
/// <see cref="ITokenizer"/> decorator that picks a tokenizer per document based on a crude
/// language guess, so mixed-language corpora can use a French stemmer on French text and an
/// English one on English text.
/// </summary>
public sealed class LanguageAwareTokenizer : ITokenizer
{
    private readonly ITokenizer _defaultTokenizer;
    private readonly ITokenizer _alternateTokenizer;
    private readonly Func<string, string> _detectLanguage;
    private readonly string _alternateLanguage;

    /// <summary>
    /// Creates a tokenizer that uses <paramref name="alternateTokenizer"/> when
    /// <paramref name="detectLanguage"/> returns <paramref name="alternateLanguage"/>, and
    /// <paramref name="defaultTokenizer"/> otherwise.
    /// </summary>
    public LanguageAwareTokenizer(
        ITokenizer defaultTokenizer,
        ITokenizer alternateTokenizer,
        Func<string, string> detectLanguage,
        string alternateLanguage = "fr")
    {
        ArgumentNullException.ThrowIfNull(defaultTokenizer);
        ArgumentNullException.ThrowIfNull(alternateTokenizer);
        ArgumentNullException.ThrowIfNull(detectLanguage);

        _defaultTokenizer = defaultTokenizer;
        _alternateTokenizer = alternateTokenizer;
        _detectLanguage = detectLanguage;
        _alternateLanguage = alternateLanguage;
    }

    /// <summary>Uses a built-in heuristic (French vs English) to pick between two tokenizers.</summary>
    public static LanguageAwareTokenizer FrenchEnglish(ITokenizer english, ITokenizer french) =>
        new(english, french, DetectFrenchEnglish, "fr");

    /// <summary>Detects French vs English using a crude heuristic.</summary>
    public static string DetectFrenchEnglish(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "en";

        // Crude: look for frequent French function words / accents.
        string lower = text.ToLowerInvariant();

        if (lower.Contains('é') || lower.Contains('è') || lower.Contains('à') || lower.Contains('ç'))
            return "fr";

        string[] frenchMarkers = [" le ", " la ", " les ", " de ", " des ", " et ", " est ", " dans ", " pour "];

        foreach (string marker in frenchMarkers)
        {
            if (lower.Contains(marker))
                return "fr";
        }

        return "en";
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Tokenize(string text)
    {
        ITokenizer tokenizer = _detectLanguage(text) == _alternateLanguage ? _alternateTokenizer : _defaultTokenizer;
        return tokenizer.Tokenize(text);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Tokenize(ReadOnlySpan<char> text) =>
        Tokenize(text.ToString());

    /// <inheritdoc />
    public void TokenizeInto(ReadOnlySpan<char> text, List<string> destination)
    {
        ITokenizer tokenizer = _detectLanguage(text.ToString()) == _alternateLanguage ? _alternateTokenizer : _defaultTokenizer;
        tokenizer.TokenizeInto(text, destination);
    }
}
