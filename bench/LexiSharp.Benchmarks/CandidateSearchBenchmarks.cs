using BenchmarkDotNet.Attributes;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;

namespace LexiSharp.Benchmarks;

/// <summary>
/// The candidate-enumeration path, which <see cref="SearchBenchmarks"/> cannot reach.
/// </summary>
/// <remarks>
/// Every row in <see cref="SearchBenchmarks"/> runs on a 38-word corpus, where each term appears
/// in roughly 74% of documents. The sum of the query terms' document frequencies therefore exceeds
/// half the corpus, the engine's own cost comparison decides that enumerating candidates is not
/// worth it, and it scans instead. Those rows are therefore blind to candidate generation — the
/// path real queries take, because real queries are made of rarer words.
/// <para>
/// This class uses a Zipf corpus so a tail term lands in a few percent of documents, which puts
/// the engine on the candidate path where it belongs. The interesting row is
/// <see cref="TwoTerms"/>: with one term the posting list already comes out in corpus order, but
/// with two the engine has to union them, and the union used to be followed by a walk of the whole
/// corpus to restore candidate order — O(corpus) however few documents matched.
/// </para>
/// <para>
/// Its own class because a benchmark class may only have one <see cref="GlobalSetupAttribute"/>,
/// and this needs a different corpus from the one <see cref="SearchBenchmarks"/> builds.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class CandidateSearchBenchmarks
{
    private const int DocumentCount = 10_000;
    private const int WordsPerDocument = 50;

    private RankedTextSearchEngine _bm25 = null!;
    private string[] _queries = Array.Empty<string>();

    [GlobalSetup]
    public void Setup()
    {
        var documents = CorpusFactory.CreateZipfDocuments(DocumentCount, WordsPerDocument);

        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
        engine.Index(documents);
        _bm25 = engine;

        // Tail terms. Each is in a few percent of the corpus, so the document union stays well
        // under the half-corpus threshold the engine compares against.
        _queries =
        [
            "w20000x",
            "w20000x w25000x",
            "w20000x w25000x w29000x",
        ];
    }

    /// <summary>One rare term: one posting list, already in corpus order, nothing to union.</summary>
    [Benchmark]
    public IReadOnlyList<SearchResult> OneTerm() => _bm25.Search(_queries[0]);

    /// <summary>Two rare terms: the engine unions two posting lists before scoring.</summary>
    [Benchmark]
    public IReadOnlyList<SearchResult> TwoTerms() => _bm25.Search(_queries[1]);

    /// <summary>Three rare terms matching a couple of documents in the whole corpus.</summary>
    [Benchmark]
    public IReadOnlyList<SearchResult> ThreeTerms() => _bm25.Search(_queries[2]);
}
