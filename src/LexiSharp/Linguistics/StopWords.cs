using System.Collections.Frozen;

namespace LexiSharp.Linguistics;

/// <summary>
/// Built-in sets of low-information words that can be removed during tokenization.
/// </summary>
public static class StopWords
{
    // org.apache.lucene.analysis.en.EnglishAnalyzer's STOP_WORDS_SET, in its own order.
    // 33 words, and "i" is deliberately absent: Lucene's StandardTokenizer emits it and its stop
    // filter keeps it. Reproduced here so a LexiSharp index can hold the same terms as a Lucene one.
    private static readonly string[] _luceneEnglish =
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
    /// The 33-word English stop word list of Lucene's <c>EnglishAnalyzer</c> — the analyzer the
    /// Anserini and Pyserini BM25 baselines use for the BEIR/Pyserini indices. Ship it when an
    /// index has to be comparable with those published numbers; <see cref="English"/> is a
    /// different, larger list and produces a measurably different index.
    /// </summary>
    /// <remarks>
    /// The two lists are not interchangeable, and the difference is not a rounding detail: measured
    /// over the NFCorpus corpus, indexing with this set holds 655,155 terms where the Anserini
    /// index of the same corpus holds 637,485 (+2.8%) and <see cref="English"/> holds 564,709
    /// (−11.4% against Anserini). BM25 effectiveness is only comparable across two runs whose
    /// analysis agrees, so a comparison against a Lucene-published number should use this set, not
    /// <see cref="English"/>. It is exposed by name, not made the default, because changing the
    /// default list would invalidate every recorded baseline in this repository.
    /// </remarks>
    public static IReadOnlySet<string> LuceneEnglish { get; } = CreateLuceneEnglish();

    private static IReadOnlySet<string> CreateLuceneEnglish() =>
        _luceneEnglish.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Returns a custom set from the given words (expected lowercase).</summary>
    public static IReadOnlySet<string> Create(params string[] words) =>
        words.ToFrozenSet(StringComparer.Ordinal);

    private static IReadOnlySet<string> CreateEnglish() =>
        _english.ToFrozenSet(StringComparer.Ordinal);
}