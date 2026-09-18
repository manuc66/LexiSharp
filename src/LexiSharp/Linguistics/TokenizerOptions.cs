namespace LexiSharp.Linguistics;

/// <summary>
/// Configuration knobs for the <see cref="Tokenizer"/>.
/// </summary>
public sealed record TokenizerOptions
{
    /// <summary>A default, conservative configuration: lowercase, strip accents, keep words of length ≥ 2.</summary>
    public static TokenizerOptions Default { get; } = new();

    /// <summary>Ignore tokens belonging to the stop word set (see <see cref="StopWords"/>). Default: <c>false</c>.</summary>
    public bool RemoveStopWords { get; init; }

    /// <summary>
    /// Optional stemming applied to each term. When null, terms are left unstemmed.
    /// No stemmer ships with the library; provide an <see cref="IStemmer"/> implementation.
    /// </summary>
    public IStemmer? Stemmer { get; init; }

    /// <summary>Smallest n-gram size produced (1 = unigrams only). Default: <c>1</c>.</summary>
    public int NGramMin { get; init; } = 1;

    /// <summary>Largest n-gram size produced. Default: <c>1</c>.</summary>
    public int NGramMax { get; init; } = 1;

    /// <summary>Keep tokens of a single character. Default: <c>false</c>.</summary>
    public bool KeepSingleCharTerms { get; init; }

    /// <summary>Custom stop word set (learned as a case-insensitive set). Ignored until <see cref="RemoveStopWords"/> is set.</summary>
    public IReadOnlySet<string>? StopWords { get; init; }

    /// <summary>The active stop word set: an explicit set if provided, otherwise <see cref="Linguistics.StopWords.English"/>.</summary>
    public IReadOnlySet<string> GetStopWords() => StopWords ?? Linguistics.StopWords.English;

    internal TokenizerOptions Sanitize() => this with
    {
        NGramMin = Math.Max(1, NGramMin),
        NGramMax = Math.Max(Math.Max(1, NGramMin), NGramMax),
    };
}