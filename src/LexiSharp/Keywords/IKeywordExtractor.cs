namespace LexiSharp.Keywords;

/// <summary>
/// A keyword extracted from a text, with its extractor-specific relevance score (higher is
/// better; the score scale depends on the extractor).
/// </summary>
/// <param name="Term">The keyword itself, normalized by the extractor's tokenizer.</param>
/// <param name="Score">Relevance score (higher is better, non-negative).</param>
public sealed record Keyword(string Term, double Score);

/// <summary>
/// Strategy for pulling the most representative terms out of a single text — tag generation,
/// query expansion seeds, summary highlights.
/// </summary>
/// <remarks>
/// Extractors are pure functions of (text, tokenizer): they never touch an index unless the
/// implementation was built with one (e.g. a corpus-backed TF-IDF that demotes words common to
/// the whole corpus). Results are ordered best-first and deterministic.
/// </remarks>
public interface IKeywordExtractor
{
    /// <summary>Human readable name of the strategy, e.g. <c>"TextRank"</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Extracts at most <paramref name="topN"/> keywords from the text, best-first.
    /// </summary>
    /// <param name="text">The text to mine.</param>
    /// <param name="topN">Maximum number of keywords to return.</param>
    IReadOnlyList<Keyword> Extract(string text, int topN = 10);
}
