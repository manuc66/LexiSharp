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
/// <see cref="HeadTerm"/>, <see cref="TwoHeadTerms"/> and <see cref="ThreeHeadTerms"/> cover the
/// other end: terms frequent enough that the union is most of the corpus. Those are the rows that
/// decide whether a search is bounded by the posting entries the query has or by the size of the
/// corpus, and no other class here reaches that regime.
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
        //
        // The head terms are the other end of the same corpus: w1x is in 95% of documents, so a
        // query built from them matches nearly everything.
        //
        // Whether *real* queries sit nearer the head or the tail is **not** established here, and
        // this class used to assert that they did without a source. It is an open question, answerable
        // from query logs or from a BEIR query set, and the answer decides how much of the table
        // above matters in production — so it is stated as open rather than assumed.
        //
        // What is measured, and does not depend on the question, is that the head rows cost the
        // document-at-a-time loop roughly a corpus walk each while the tail rows cost it almost
        // nothing. The speedup tracks the cost that loop already had.
        _queries =
        [
            "w20000x",
            "w20000x w25000x",
            "w20000x w25000x w29000x",
            "w1x",
            "w1x w2x",
            "w1x w2x w3x",
        ];
    }

    /// <summary>One rare term: one posting list, already in corpus order, nothing to union.</summary>
    [Benchmark(Baseline = true)]
    public IReadOnlyList<SearchResult> OneTerm() => _bm25.Search(_queries[0]);

    /// <summary>Two rare terms: the engine unions two posting lists before scoring.</summary>
    [Benchmark]
    public IReadOnlyList<SearchResult> TwoTerms() => _bm25.Search(_queries[1]);

    /// <summary>Three rare terms matching a couple of documents in the whole corpus.</summary>
    [Benchmark]
    public IReadOnlyList<SearchResult> ThreeTerms() => _bm25.Search(_queries[2]);

    /// <summary>One term in 95% of the corpus: the whole posting list has to be walked.</summary>
    [Benchmark]
    public IReadOnlyList<SearchResult> HeadTerm() => _bm25.Search(_queries[3]);

    /// <summary>Two such terms: ~18,500 posting entries for 10,000 candidate documents.</summary>
    [Benchmark]
    public IReadOnlyList<SearchResult> TwoHeadTerms() => _bm25.Search(_queries[4]);

    /// <summary>Three such terms: ~26,500 posting entries — more work than the corpus holds.</summary>
    [Benchmark]
    public IReadOnlyList<SearchResult> ThreeHeadTerms() => _bm25.Search(_queries[5]);
}
