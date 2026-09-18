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
    }

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
}