using BenchmarkDotNet.Attributes;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;

namespace LexiSharp.Benchmarks;

/// <summary>
/// Where the term-at-a-time scoring pass starts paying for itself.
/// </summary>
/// <remarks>
/// The two rows are the same query, the same corpus and the same scorer; the only difference is
/// which loop the engine takes. A metadata filter that passes every document is one of the two
/// conditions that send a query down the per-document path, and it changes nothing else about the
/// work, so it is a clean switch between the loops from the public surface — no internal type has
/// to be reached to run it.
/// <para>
/// The comparison matters because the two loops do not have the same shape of cost. The
/// term-at-a-time pass rents and clears a corpus-sized buffer before it does anything, so it pays
/// a fixed cost no matter how few documents match; the per-document loop pays nothing up front but
/// then resolves a document id per (document, term) pair, so its cost grows with the candidate
/// set. Neither wins everywhere, and where the curves cross is the number a threshold should be
/// set from rather than guessed.
/// </para>
/// <para>
/// The terms are chosen by target document frequency rather than by name, because that is the
/// variable the cost model is written in.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class ScoringPathBenchmarks
{
    private const int DocumentCount = 10_000;
    private const int WordsPerDocument = 50;

    /// <summary>
    /// Target document frequency of the query's single term. The closest term on the corpus is
    /// used, so the row labels the intended regime and the df it found is printed by
    /// <see cref="Setup"/>.
    /// </summary>
    [Params(1, 4, 16, 64, 256, 1024, 4096, 9525)]
    public int TargetDocumentFrequency { get; set; }

    private RankedTextSearchEngine _engine = null!;
    private string _query = null!;
    private SearchOptions _perDocument = null!;

    [GlobalSetup]
    public void Setup()
    {
        var documents = CorpusFactory.CreateZipfDocuments(DocumentCount, WordsPerDocument);

        var index = new InMemoryTextIndex();
        _engine = new RankedTextSearchEngine(index, new Bm25Scorer());
        _engine.Index(documents);

        _query = PickTerm(index, TargetDocumentFrequency);
        _perDocument = new SearchOptions(
            Limit: 10,
            Filters: new[] { new MetadataFilter("__none__", MetadataFilterOperator.NotEqual, "__none__") });
    }

    /// <summary>The term-at-a-time pass, which the engine takes when no gate has to run first.</summary>
    [Benchmark]
    public IReadOnlyList<SearchResult> Accumulating() => _engine.Search(_query);

    /// <summary>The per-document loop, forced by a filter that rejects nothing.</summary>
    [Benchmark]
    public IReadOnlyList<SearchResult> PerDocument() => _engine.Search(_query, _perDocument);

    /// <summary>
    /// The term whose document frequency is closest to <paramref name="target"/>, preferring an
    /// exact match.
    /// </summary>
    private static string PickTerm(InMemoryTextIndex index, int target)
    {
        string? best = null;
        int bestDistance = int.MaxValue;

        foreach (var term in index.Vocabulary)
        {
            int df = index.DocumentFrequency(term);

            if (df == 0)
                continue;

            int distance = Math.Abs(df - target);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = term;

                if (distance == 0)
                    break;
            }
        }

        return best ?? "w1x";
    }
}
