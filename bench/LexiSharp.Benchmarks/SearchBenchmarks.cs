using BenchmarkDotNet.Attributes;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;

namespace LexiSharp.Benchmarks;

[MemoryDiagnoser]
public class SearchBenchmarks
{
    private const int DocumentCount = 10_000;
    private const int WordsPerDocument = 50;

    private RankedTextSearchEngine _bm25 = null!;
    private RankedTextSearchEngine _tfIdf = null!;
    private RankedTextSearchEngine _queryLikelihood = null!;
    private RankedTextSearchEngine _boolean = null!;
    private string[] _queries = Array.Empty<string>();

    private static RankedTextSearchEngine Engine(ITextScorer scorer, SearchDocument[] documents)
    {
        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), scorer);
        engine.Index(documents);
        return engine;
    }

    [Benchmark]
    public IReadOnlyList<SearchResult> Bm25Search() => _bm25.Search(_queries[0]);

    [Benchmark]
    public IReadOnlyList<SearchResult> TfIdfSearch() => _tfIdf.Search(_queries[0]);

    [Benchmark]
    public IReadOnlyList<SearchResult> QueryLikelihoodSearch() => _queryLikelihood.Search(_queries[0]);

    [Benchmark]
    public IReadOnlyList<SearchResult> BooleanSearch() => _boolean.Search(_queries[0]);

    [Benchmark]
    public IReadOnlyList<SearchResult> Bm25SearchRunAllQueries()
    {
        IReadOnlyList<SearchResult> last = Array.Empty<SearchResult>();
        foreach (var query in _queries)
            last = _bm25.Search(query);
        return last;
    }

    // --- Tracing ------------------------------------------------------------------------------
    // SearchTrace is opt-in through SearchOptions.Trace and defaults to null. These benchmarks
    // exist to measure what that null costs: the delta against Bm25Search is the whole price of
    // the feature, in both regimes a caller can be in.
    //   TraceSaturated - one long-lived trace that has hit Capacity, so recording is a counter
    //                    increment. The cheapest a trace can be.
    //   TracePerSearch - a fresh trace per search, which is the documented per-request usage and
    //                    therefore also pays for the backing list allocation.

    private SearchTrace _saturatedTrace = null!;
    private SearchOptions _saturatedOptions = null!;

    [GlobalSetup]
    public void Setup()
    {
        var documents = CorpusFactory.CreateDocuments(DocumentCount, WordsPerDocument);

        _bm25 = Engine(new Bm25Scorer(), documents);
        _tfIdf = Engine(new TfIdfScorer(), documents);
        _queryLikelihood = Engine(new QueryLikelihoodScorer(), documents);
        _boolean = Engine(new BooleanScorer(BooleanMatch.AnyTerm), documents);

        _queries = new[]
        {
            "search engine",
            "alpha bravo charlie",
            "query rank score",
            "document token term corpus",
            "zulu whiskey tango",
        };

        // Pre-filled to capacity so the benchmark measures the steady state, not the warmup.
        _saturatedTrace = new SearchTrace(capacity: 10);
        _saturatedOptions = new SearchOptions(Limit: 10, Trace: _saturatedTrace);
        for (int i = 0; i < 100; i++)
            _bm25.Search(_queries[0], _saturatedOptions);
    }

    /// <summary>Identical to <see cref="Bm25Search"/>, with a long-lived trace already at capacity.</summary>
    [Benchmark]
    public IReadOnlyList<SearchResult> TraceSaturated() => _bm25.Search(_queries[0], _saturatedOptions);

    /// <summary>Identical to <see cref="Bm25Search"/>, with a fresh trace per search.</summary>
    [Benchmark]
    public IReadOnlyList<SearchResult> TracePerSearch() =>
        _bm25.Search(_queries[0], new SearchOptions(Limit: 10, Trace: new SearchTrace(capacity: 10)));
}