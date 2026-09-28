using System.Collections.Frozen;

// CA1859 ("use a concrete type instead of the interface") is suppressed on the lines
// below. The interface is the published return type: a List<T> or an array in its place
// would hand callers a mutable collection through a contract that says they cannot have
// one, and what it saves is a single interface dispatch per call, which no measurement in
// docs/benchmarks.md attributes time to.

namespace LexiSharp.Linguistics;

/// <summary>
/// Built-in sets of low-information words that can be removed during tokenization.
/// </summary>
public static class StopWords
{
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

    /// <summary>Returns a custom set from the given words (expected lowercase).</summary>
    public static IReadOnlySet<string> Create(params string[] words) =>
        words.ToFrozenSet(StringComparer.Ordinal);

    private static IReadOnlySet<string> CreateEnglish() => // NOSONAR:CA1859
        _english.ToFrozenSet(StringComparer.Ordinal);
}