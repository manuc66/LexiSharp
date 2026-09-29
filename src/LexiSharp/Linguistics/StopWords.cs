using System.Collections.Frozen;

namespace LexiSharp.Linguistics;

/// <summary>
/// Built-in sets of low-information words that can be removed during tokenization.
/// </summary>
public static class StopWords
{
    // The 33 function words of the conventional English retrieval stop list, in a stable order.
    // "i" is deliberately absent: it is a word here, not a stop word, and dropping single-character
    // terms is a separate decision the tokenizer makes.
    private static readonly string[] _englishFunction =
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by",
        "for", "if", "in", "into", "is", "it", "no", "not", "of",
        "on", "or", "such", "that", "the", "their", "then", "there",
        "these", "they", "this", "to", "was", "will", "with",
    };

    private static readonly string[] _english =
    {
        "a", "about", "above", "after", "again", "against", "all", "am", "an",
        "and", "any", "are", "as", "at", "be", "because", "been", "before",
        "being", "below", "between", "both", "but", "by", "can", "did", "do",
        "does", "doing", "down", "during", "each", "few", "for", "from", "further",
        "had", "has", "have", "having", "he", "her", "here", "hers", "herself",
        "him", "himself", "his", "how", "i", "if", "in", "into", "is", "it",
        "its", "itself", "just", "me", "more", "most", "my", "myself", "no",
        "nor", "not", "now", "of", "off", "on", "once", "only", "or", "other",
        "our", "ours", "ourselves", "out", "over", "own", "same", "she",
        "should", "so", "some", "such", "than", "that", "the", "their", "theirs",
        "them", "themselves", "then", "there", "these", "they", "this", "those",
        "through", "to", "too", "under", "until", "up", "very", "was", "we",
        "were", "what", "when", "where", "which", "while", "who", "whom", "why",
        "will", "with", "you", "your", "yours", "yourself", "yourselves",
    };

    /// <summary>
    /// A compact English stop word list (function words: articles, pronouns, conjugations,
    /// auxiliaries, common prepositions).
    /// </summary>
    public static IReadOnlySet<string> English { get; } = CreateEnglish();

    /// <summary>
    /// A short English stop word list: 33 function words, and nothing else. Ship it when an index
    /// has to be comparable with a published BM25 measurement — the conventional English analysis
    /// most of those numbers were produced with is a stemmer plus a list of this size, and
    /// <see cref="English"/> is a different, larger list that produces a measurably different index.
    /// </summary>
    /// <remarks>
    /// The two lists are not interchangeable, and the difference is not a rounding detail. Measured
    /// over the NFCorpus corpus, indexing with this set holds 655,155 terms against the 637,485 a
    /// published reference index of the same corpus holds (+2.8%); <see cref="English"/> holds
    /// 564,709, which is 11.4% short of it. BM25 effectiveness is only comparable across two runs
    /// whose analysis agrees, so a comparison against a published figure should use this set, not
    /// <see cref="English"/>. It is exposed by name rather than made the default, because changing
    /// the default list would invalidate every recorded baseline in this repository.
    /// </remarks>
    public static IReadOnlySet<string> EnglishFunction { get; } = CreateEnglishFunction();

    private static IReadOnlySet<string> CreateEnglishFunction() =>
        _englishFunction.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Returns a custom set from the given words (expected lowercase).</summary>
    public static IReadOnlySet<string> Create(params string[] words) =>
        words.ToFrozenSet(StringComparer.Ordinal);

    private static IReadOnlySet<string> CreateEnglish() =>
        _english.ToFrozenSet(StringComparer.Ordinal);
}