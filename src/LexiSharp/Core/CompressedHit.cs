namespace LexiSharp.Core;

/// <summary>
/// One compressed hit of <see cref="ICompressingSearchEngine.SearchCompressed"/>: the ranked
/// document's id and score, its text reduced to the query-relevant content, and how much of
/// the source that content retains.
/// </summary>
/// <param name="DocumentId">Id of the matched document.</param>
/// <param name="Score">The score the underlying engine assigned — unchanged by compression.</param>
/// <param name="CompressedText">The reduced text, as the compressor produced it.</param>
/// <param name="CompressionRatio">
/// Fraction of the source text the compressed text retains (0 when the source is empty):
/// <c>CompressedText.Length / source.Length</c>. 1 means nothing was dropped; 0 means the
/// compressor kept nothing.
/// </param>
public sealed record CompressedHit(
    string DocumentId,
    double Score,
    string CompressedText,
    double CompressionRatio);