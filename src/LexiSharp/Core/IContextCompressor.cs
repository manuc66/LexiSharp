namespace LexiSharp.Core;

/// <summary>
/// Reduces a document's text to the content a query actually cares about — the token-reduction
/// seam between a ranked page and a generation model. <b>LexiSharp never runs a model
/// itself</b>: a learned compressor (a small ONNX perplexity model, an LLM rewrite) is
/// consumer code; the model-free <see cref="LexiSharp.Compression.QueryWindowCompressor"/>
/// makes the seam testable offline.
/// </summary>
/// <remarks>
/// The compressor is <b>query-aware</b>: it receives the query's terms so it can keep the
/// passages that answer them and drop the rest. The contract returns text only — how much of
/// the source was retained is measured by the consuming engine
/// (<see cref="LexiSharp.Compression.CompressingTextSearchEngine"/>), which owns one
/// definition of <c>CompressionRatio</c> instead of letting every implementation drift.
/// <para>
/// Extractive by nature here: a compressor that finds no query term keeps nothing, which is
/// the honest reading of "no query-relevant content". A generative rewriter is a consumer
/// implementation of the same seam.
/// </para>
/// </remarks>
public interface IContextCompressor
{
    /// <summary>Human readable name of the strategy, e.g. <c>"query-window"</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Compresses <paramref name="documentText"/> so that only the content relevant to
    /// <paramref name="queryTerms"/> remains.
    /// </summary>
    /// <param name="documentText">A retrieved document's text, as the engine returned it.</param>
    /// <param name="queryTerms">The query's terms, as the engine tokenized them.</param>
    string Compress(string documentText, IReadOnlyList<string> queryTerms);
}