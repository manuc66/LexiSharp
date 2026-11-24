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

    /// <summary>
    /// Detects French vs English using character n-gram profiles (n = 2 and 3), falling back to
    /// a function-word/accents heuristic for very short texts.
    /// </summary>
    public static string DetectFrenchEnglish(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "en";

        string lower = text.ToLowerInvariant();

        // Short titles ("IP-1883 fix") are too sparse for n-gram profiles: use markers.
        if (lower.Length < 20)
        {
            return DetectByMarkers(lower);
        }

        Dictionary<string, double> inputProfile = BuildProfile(lower);
        double frenchSimilarity = ProfileSimilarity(inputProfile, FrenchProfile);
        double englishSimilarity = ProfileSimilarity(inputProfile, EnglishProfile);

        if (Math.Abs(frenchSimilarity - englishSimilarity) < 0.02)
        {
            return DetectByMarkers(lower);
        }

        return frenchSimilarity > englishSimilarity ? "fr" : "en";
    }

    private static string DetectByMarkers(string lower)
    {
        if (lower.Contains('é') || lower.Contains('è') || lower.Contains('à') || lower.Contains('ç') ||
            lower.Contains('ù') || lower.Contains('ô') || lower.Contains('î') || lower.Contains('ë'))
            return "fr";

        string[] frenchMarkers =
        [
            " le ", " la ", " les ", " de ", " des ", " et ", " est ", " dans ", " pour ",
            " avec ", " sur ", " une ", " un ", " du ", " au ", " aux ", " que ", " qui ",
            " sont ", " mais ", " ou ", " où ", " très ", " être ", " avoir ",
        ];

        string[] englishMarkers =
        [
            " the ", " and ", " of ", " to ", " in ", " is ", " are ", " for ", " with ",
            " on ", " at ", " by ", " this ", " that ", " these ", " those ", " be ", " have ",
        ];

        int frenchScore = 0;
        int englishScore = 0;
        foreach (string marker in frenchMarkers)
        {
            if (lower.Contains(marker))
            {
                frenchScore++;
            }
        }

        foreach (string marker in englishMarkers)
        {
            if (lower.Contains(marker))
            {
                englishScore++;
            }
        }

        return frenchScore > englishScore ? "fr" : "en";
    }

    private static double ProfileSimilarity(Dictionary<string, double> input, IReadOnlyDictionary<string, double> profile)
    {
        double dot = 0;
        double normInput = 0;
        double normProfile = 0;

        foreach ((string gram, double value) in input)
        {
            normInput += value * value;
            if (profile.TryGetValue(gram, out double reference))
            {
                dot += value * reference;
            }
        }

        foreach (double value in profile.Values)
        {
            normProfile += value * value;
        }

        if (normInput <= 0 || normProfile <= 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(normInput) * Math.Sqrt(normProfile));
    }

    private static Dictionary<string, double> BuildProfile(string text)
    {
        var profile = new Dictionary<string, double>(StringComparer.Ordinal);
        string letters = new string(text.Where(char.IsLetter).ToArray());
        int limit = Math.Min(letters.Length, 2000);

        for (int n = 2; n <= 3; n++)
        {
            for (int i = 0; i + n <= limit; i++)
            {
                string gram = letters.Substring(i, n);
                profile[gram] = profile.TryGetValue(gram, out double count) ? count + 1 : 1;
            }
        }

        return profile;
    }

    private static readonly IReadOnlyDictionary<string, double> FrenchProfile = BuildProfile(
        "le la les de des du et est dans pour avec sur une un que qui sont mais ou très être avoir " +
        "ce cette ces il elle nous vous ils elles pas plus comme aussi tout faire dire voir savoir " +
        "pouvoir vouloir devoir prendre donner aller venir partir rester trouver penser demander " +
        "réponse question travail journée semaine année aujourd'hui demain hier matin soir");

    private static readonly IReadOnlyDictionary<string, double> EnglishProfile = BuildProfile(
        "the and of to in is are for with on at by this that these those be have has had do does did " +
        "will would can could should may might must shall not no yes all any some more most other " +
        "such only own same so than too very just because as until while about against between " +
        "through during before after above below from up down out off over under again further " +
        "then once here there when where why how");

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
