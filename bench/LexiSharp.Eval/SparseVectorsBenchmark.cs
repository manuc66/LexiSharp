using System.Globalization;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;

namespace LexiSharp.Eval;

/// <summary>
/// The sparse row: a directory of vectors produced outside this harness, loaded into
/// <see cref="SparseTextSearchEngine"/> and scored by the same query list, qrels and metric code as
/// every other row.
/// </summary>
/// <remarks>
/// The library never runs a model, so a sparse row cannot encode anything. This lane is the consumer
/// side of <see cref="ISparseEmbeddingProvider"/>: the vectors come from
/// <c>bench/LexiSharp.Eval/sparse/encode_splade.py</c> (an ONNX export of SPLADE-v3, run once, on a
/// host with the model), and the row measures what the engine does with them. The encode cost is not
/// in the row's time column because there is no time column, and it is not in its metric either.
/// </remarks>
/// <param name="VectorsDirectory">Where <c>&lt;corpus&gt;.meta.json</c> and the two vector files live.</param>
internal sealed record SparseLane(string VectorsDirectory)
{
    /// <summary>Parses <c>--sparse</c>, refusing a directory that is not there rather than at load time.</summary>
    public static SparseLane Parse(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return new SparseLane(Path.GetFullPath(directory));
    }
}

/// <summary>One sparse row: what was measured, over how many documents, and what it does not say.</summary>
/// <param name="Description">The model, truncation and top-k, read from the producer's manifest.</param>
/// <param name="Documents">Documents indexed.</param>
/// <param name="TermsPerDocument">Mean non-zero terms per document, as the encoder recorded them.</param>
/// <param name="Result">The metrics.</param>
/// <param name="Caveats">Statements the row depends on, printed with it.</param>
internal sealed record SparseMeasurement(
    string Description,
    int Documents,
    double TermsPerDocument,
    ConfigResult Result,
    IReadOnlyList<string> Caveats);

/// <summary>Loads a vector set, indexes it, and scores the corpus's test queries.</summary>
internal static class SparseBenchmark
{
    /// <summary>
    /// Measures the sparse engine over precomputed vectors.
    /// </summary>
    /// <exception cref="SparseVectorsException">
    /// The vectors are missing, malformed, or do not cover every document or test query — each
    /// reported with the count and an example, because a metric over the covered subset would be a
    /// different query set from every row beside it.
    /// </exception>
    public static SparseMeasurement Measure(
        SparseLane lane,
        BeirCorpus corpus,
        string dataset,
        int topK,
        int? limit,
        NdcgGain ndcgGain,
        bool excludeQueryDocument = false,
        int jobs = 1)
    {
        ArgumentNullException.ThrowIfNull(lane);
        ArgumentNullException.ThrowIfNull(corpus);

        SparseVectorSet vectors = SparseVectorSet.Load(lane.VectorsDirectory, dataset);

        // The same text rule the encoder hashed, applied by the same code path that builds the
        // index: title in front of the body, because the sparse engine has no fields.
        var documents = corpus.Documents
            .Select(document => new SearchDocument(
                document.Id,
                SparseVectorSet.DocumentText(document.Title, document.Text)))
            .ToList();

        var entries = new List<SparseIndexEntry>(documents.Count);
        var missing = new List<string>();

        foreach (SearchDocument document in documents)
        {
            string digest = SparseVectorSet.HashText(document.Text);

            if (vectors.Documents.TryGetValue(digest, out IReadOnlyDictionary<string, float>? weights))
                entries.Add(new SparseIndexEntry(document, weights));
            else
                missing.Add(document.Id);
        }

        if (missing.Count > 0)
        {
            throw new SparseVectorsException(
                $"{missing.Count:N0} of {documents.Count:N0} {dataset} documents have no vector in "
                + $"'{lane.VectorsDirectory}' (for example document '{missing[0]}'), so the index would "
                + "silently omit them and the metric would be read over a smaller corpus than the row "
                + "beside it. Re-encode this corpus, or point --sparse at its vectors.");
        }

        List<EvaluatedQuery> queries = Evaluation.BuildQueries(corpus, limit, excludeQueryDocument);

        var missingQueries = queries
            .Where(query => !vectors.Queries.ContainsKey(SparseVectorSet.HashText(query.Query.Text)))
            .Select(query => query.Query.Id)
            .ToList();

        if (missingQueries.Count > 0)
        {
            throw new SparseVectorsException(
                $"{missingQueries.Count:N0} of {queries.Count:N0} test queries in {dataset} have no "
                + $"vector in '{lane.VectorsDirectory}' (for example query '{missingQueries[0]}'). A query with "
                + "no vector matches nothing, which reads as a ranking result rather than as a missing "
                + "file, so no row is produced. Re-encode this corpus's queries.jsonl.");
        }

        var engine = new SparseTextSearchEngine(new SparseVectorProvider(vectors));

        // Import, not Index: the weights were computed once, outside, and re-embedding the corpus
        // through the provider would be the same lookup done twice.
        engine.Import(entries);

        ConfigResult result = Evaluation.RunEngine(
            corpus, engine, vectors.Manifest.Description, topK, limit, ndcgGain,
            excludeQueryDocument, jobs);

        var caveats = new List<string>
        {
            $"the vectors were produced offline by bench/LexiSharp.Eval/sparse/encode_splade.py from "
            + $"{vectors.Manifest.ModelFile} (sha256 {vectors.Manifest.ModelSha256[..16]}…) and this "
            + "row's metrics contain no encode cost.",
            "documents are encoded as title + newline + body, because the sparse engine has no field "
            + "concept: unlike the BM25F rows, no title weight exists in this one.",
        };

        if (vectors.Manifest.Quantised)
        {
            caveats.Add(
                "the export is the 8-bit weight-quantised file: its sparse weights are not the "
                + "full-precision model's, and this row is not comparable with a full-precision one.");
        }

        if (vectors.Manifest.TopK > 0)
        {
            caveats.Add(
                $"each text keeps its {vectors.Manifest.TopK} highest-weighted terms; the export "
                + "emits more non-zero terms than that for some documents, and those were dropped "
                + "rather than scored.");
        }

        return new SparseMeasurement(
            vectors.Manifest.Description,
            documents.Count,
            vectors.Manifest.TermsPerDocument,
            result,
            caveats);
    }

    /// <summary>The row as one block of text, in the shape the backend rows print.</summary>
    public static string Format(SparseMeasurement measurement, string dataset)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        ConfigResult result = measurement.Result;

        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"== {dataset}, sparse =="),
            string.Create(CultureInfo.InvariantCulture, $"Model:  {measurement.Description}"),
            string.Create(
                CultureInfo.InvariantCulture,
                $"Index:  {measurement.Documents:N0} documents in SparseTextSearchEngine, "
                + $"{measurement.TermsPerDocument:0.0} terms per document"),
            string.Create(
                CultureInfo.InvariantCulture,
                $"Result: nDCG@10 {result.NdcgAt10:0.0000} | MAP@10 {result.MapAt10:0.0000} | "
                + $"MRR@10 {result.MrrAt10:0.0000} | R@10 {result.RecallAt10:0.0000} | "
                + $"{result.Queries:N0} queries"),
        };

        return string.Join('\n', lines) + "\n";
    }
}
