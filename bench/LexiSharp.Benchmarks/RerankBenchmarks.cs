using BenchmarkDotNet.Attributes;
using LexiSharp.Core;
using LexiSharp.Hybrid;

namespace LexiSharp.Benchmarks;

/// <summary>
/// The two embedding rerankers, on the shapes they are meant for.
/// </summary>
/// <remarks>
/// Both are second-stage rerankers over a shortlist, so the cost that matters is per candidate,
/// not per corpus: <see cref="MaxSim"/> walks a query-token × document-token matrix of cosines per
/// candidate, and <see cref="Mmr"/> compares every remaining candidate against every pick.
/// <para>
/// What these rows are for. Both inner loops used to recompute a vector's norm for every pair —
/// once per (query token, document token) cosine, and once per (candidate, selected) comparison —
/// which is a norm multiplied by the pair count rather than by the number of vectors. Resolved once
/// per vector, the same arithmetic produces the same doubles (the norms are the values the cosine
/// already divided by) and the pair loops lose two thirds of their passes.
/// </para>
/// <para>
/// The vectors are synthetic and fixed-seed: random directions at a realistic dimensionality, which
/// is what makes the row a measurement of the loop rather than of a model. Both rerankers are pure
/// arithmetic over what the caller hands them, so a real encoder changes the vectors and not the
/// work per pair.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class RerankBenchmarks
{
    /// <summary>Candidates offered to the reranker — the shortlist a cascade hands to its last stage.</summary>
    [Params(100, 500)]
    public int Candidates { get; set; }

    private const int QueryTokens = 8;
    private const int DocumentTokens = 32;
    private const int MaxSimDimension = 128;
    private const int MmrDimension = 384;

    private MaxSimReranker _maxSim = null!;
    private MaximalMarginalRelevanceReranker _mmr = null!;
    private IReadOnlyList<SearchResult> _results = null!;

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(11);
        var query = new ReadOnlyMemory<float>[QueryTokens];

        for (int i = 0; i < QueryTokens; i++)
            query[i] = RandomVector(random, MaxSimDimension);

        var tokenVectors = new Dictionary<string, IReadOnlyList<ReadOnlyMemory<float>>>(Candidates, StringComparer.Ordinal);
        var vectors = new Dictionary<string, ReadOnlyMemory<float>>(Candidates, StringComparer.Ordinal);
        var results = new List<SearchResult>(Candidates);

        for (int c = 0; c < Candidates; c++)
        {
            string id = "doc-" + c.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var tokens = new ReadOnlyMemory<float>[DocumentTokens];

            for (int t = 0; t < DocumentTokens; t++)
                tokens[t] = RandomVector(random, MaxSimDimension);

            tokenVectors[id] = tokens;
            vectors[id] = RandomVector(random, MmrDimension);
            results.Add(new SearchResult(id, 1.0 - (c * 0.001), new SearchDocument(id, "text")));
        }

        _results = results;
        _maxSim = new MaxSimReranker(new FixedTokenModel(query), tokenVectors);
        _mmr = new MaximalMarginalRelevanceReranker(vectors, lambda: 0.7, limit: 10);
    }

    /// <summary>Late interaction over the token matrix: <c>Candidates × QueryTokens × DocumentTokens</c> cosines.</summary>
    [Benchmark]
    public IReadOnlyList<SearchResult> MaxSim() => _maxSim.Rerank("query", _results);

    /// <summary>Diversity selection: each pick is compared against every remaining candidate and every pick.</summary>
    [Benchmark]
    public IReadOnlyList<SearchResult> Mmr() => _mmr.Rerank("query", _results);

    private static ReadOnlyMemory<float> RandomVector(Random random, int dimension)
    {
        var vector = new float[dimension];

        for (int i = 0; i < dimension; i++)
            vector[i] = (float)(random.NextDouble() - 0.5);

        return vector;
    }

    /// <summary>A fixed query encoding: the reranker embeds the query once, so a model adds nothing per candidate.</summary>
    private sealed class FixedTokenModel(ReadOnlyMemory<float>[] query) : ITokenEmbeddingProvider
    {
        public string Name => "fixed";

        public IReadOnlyList<ReadOnlyMemory<float>> GetTokenEmbeddings(string text, EmbeddingUse use) => query;
    }
}
