namespace LexiSharp.Core;

/// <summary>
/// Augments the searchable text of a <see cref="SearchDocument"/> before it is indexed — the
/// « contextual retrieval » seam. A chunk that only says <i>"the benefit rose 12%"</i> is
/// reworded at index time so the corpus that is scored also carries the company and year the
/// chunk belongs to; the caller's original document stays the one results display.
/// <b>LexiSharp never runs such a model itself</b>: implementing this interface is up to the
/// consumer — a local LLM over ONNX, an HTTP call to a model server, a rule that copies
/// <see cref="SearchDocument.Fields"/> over the text — and the index built on top only reads
/// the returned document.
/// </summary>
/// <remarks>
/// The enricher sees the whole document, so it can read the global context out of
/// <see cref="SearchDocument.Fields"/> or <see cref="SearchDocument.TextFields"/> (a title, a
/// chapter) or hold its own application-level context. It rewrites
/// <see cref="SearchDocument.Text"/> — and may add fields or rewrite text fields — but must
/// keep the <see cref="SearchDocument.Id"/>: the index keys the enriched corpus by the id the
/// caller supplied and rejects a change.
/// <para>
/// Because <see cref="ITextSearchEngine.Add"/> is synchronous, a model-backed implementation
/// blocks on its async work — the same trade-off the PostgreSQL engines,
/// <see cref="ICrossEncoderScorer"/> and <see cref="ITokenEmbeddingProvider"/> already make.
/// Enrichment runs once per <c>Add</c>/<c>Index</c>, never at search time.
/// </para>
/// </remarks>
public interface IChunkContextEnricher
{
    /// <summary>Human readable name of the underlying strategy, e.g. <c>"context-injection-v1"</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Returns the document to index: the same <see cref="SearchDocument.Id"/>, with the
    /// searchable text rewritten (and, optionally, fields or text fields added) so that
    /// scoring sees what the caller wants scored.
    /// </summary>
    /// <param name="document">The caller's document, about to be indexed.</param>
    SearchDocument Enrich(SearchDocument document);
}