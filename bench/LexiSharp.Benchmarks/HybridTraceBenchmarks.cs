using BenchmarkDotNet.Attributes;
using LexiSharp.Core;
using LexiSharp.Embeddings;
using LexiSharp.Hybrid;
using LexiSharp.Indexing;
using LexiSharp.Ranking;

namespace LexiSharp.Benchmarks;

/// <summary>
/// What tracing a hybrid search costs, and what the merge explanation inside it costs.
/// </summary>
/// <remarks>
/// <para>
/// The untraced case is the one paid in production, and the trace is read from
/// <see cref="SearchOptions"/> per search, so the two rows differ only by whether anyone asked for
/// an explanation. The difference is the whole price of the feature: tracing already writes one
/// step per document of the page, and <see cref="MergeExplanation"/> adds one step per search.
/// </para>
/// <para>
/// The merger here is a weighted one on purpose — a rank merger does not implement
/// <see cref="IMergeExplainer"/>, so its traced row would not exercise the new path at all.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class HybridTraceBenchmarks
{
    private const int DocumentCount = 2_000;

    private HybridTextSearchEngine _hybrid = null!;

    [GlobalSetup]
    public void Setup()
    {
        SearchDocument[] corpus = SyntheticCorpus(DocumentCount);

        var lexical = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
        var dense = new InMemoryVectorSearchEngine(new HashingEmbeddingProvider());

        lexical.Index(corpus);
        dense.Index(corpus);

        _hybrid = new HybridTextSearchEngine(
            [lexical, dense],
            new WeightedScoreResultMerger(1, 1),
            sourceNames: ["lexical", "semantic"]);
    }

    [Benchmark(Baseline = true)]
    public int UntracedSearch() => _hybrid.Search(Query, new SearchOptions(Limit: 10)).Count;

    [Benchmark]
    public int TracedSearch()
    {
        var trace = new SearchTrace();
        return _hybrid.Search(Query, new SearchOptions(Limit: 10, Trace: trace)).Count;
    }

    private const string Query = "alpha beta gamma";

    private static SearchDocument[] SyntheticCorpus(int count)
    {
        // A Zipf-ish vocabulary so the lexical engine returns a realistic number of candidates
        // rather than either everything or nothing.
        string[] vocabulary = new string[500];

        for (int i = 0; i < vocabulary.Length; i++)
            vocabulary[i] = $"term{i}";

        var documents = new SearchDocument[count];
        var random = new Random(20261024);

        for (int i = 0; i < count; i++)
        {
            var words = new string[40];

            for (int word = 0; word < words.Length; word++)
            {
                // The first 3 terms are common; the rest are drawn from the long tail.
                int rank = word < 3 ? word : 3 + random.Next(vocabulary.Length - 3);
                words[word] = rank == 0 ? "alpha" : rank == 1 ? "beta" : rank == 2 ? "gamma" : vocabulary[rank];
            }

            documents[i] = new SearchDocument($"doc-{i}", string.Join(' ', words));
        }

        return documents;
    }
}
