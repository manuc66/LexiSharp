using BenchmarkDotNet.Attributes;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;

namespace LexiSharp.Benchmarks;

/// <summary>
/// Where the bytes of one query go. <see cref="SearchBenchmarks"/> reports a single figure per
/// scorer (~1.3 KB per BM25 search on this corpus); this class takes that figure apart by
/// isolating the components a search is made of, so a reduction attempt knows which component to
/// aim at.
/// </summary>
/// <remarks>
/// Read the <c>Allocated</c> column only — the same instruction the rest of this page carries:
/// byte counts do not depend on how fast the host is, and this machine's timings drift by more
/// than the effects being chased.
/// <para>
/// The components are measured through the public surface, so each row is what a caller would pay
/// for that operation on its own, and the differences between rows are the derived figures:
/// the three page sizes (1, 10, 100) give the cost of a returned row as a slope and the cost of
/// everything before the first row as an intercept, and <see cref="SearchMissLimit10"/> is the
/// fixed cost of a query that parses, plans and window-sizes but returns nothing and scores
/// nothing.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class QueryAllocationBreakdownBenchmarks
{
    private const int DocumentCount = 10_000;
    private const int WordsPerDocument = 50;

    private const string Hit = "search engine";
    private const string Miss = "quokka wombat";   // absent from the corpus's 38-word vocabulary
    private const string Phrase = "\"search engine\"";

    private RankedTextSearchEngine _bm25 = null!;
    private SearchOptions _limit1 = null!;
    private SearchOptions _limit10 = null!;
    private SearchOptions _limit100 = null!;
    private SearchOptions _literalLimit10 = null!;

    [GlobalSetup]
    public void Setup()
    {
        var documents = CorpusFactory.CreateDocuments(DocumentCount, WordsPerDocument);

        _bm25 = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
        _bm25.Index(documents);

        _limit1 = new SearchOptions(Limit: 1);
        _limit10 = new SearchOptions(Limit: 10);
        _limit100 = new SearchOptions(Limit: 100);
        _literalLimit10 = new SearchOptions(Limit: 10, ParseQuerySyntax: false);

        // Warm the pooled accumulator and the derived posting copies so every row below is the
        // steady state rather than the first touch.
        for (int i = 0; i < 100; i++)
        {
            _bm25.Search(Hit, _limit1);
            _bm25.Search(Hit, _limit10);
            _bm25.Search(Hit, _limit100);
            _bm25.Search(Miss, _limit10);
            _bm25.Search(Phrase, _limit10);
        }
    }

    /// <summary>The tokenizer alone: one `List<string>` backing array plus one string per token.</summary>
    [Benchmark]
    public IReadOnlyList<string> Tokenize() => Tokenizer.Default.Tokenize(Hit);

    /// <summary>
    /// One token, where <see cref="Tokenize"/> has two: the pair separates the tokenizer's fixed
    /// cost (the list it builds into) from its cost per token (the string it emits).
    /// </summary>
    [Benchmark]
    public IReadOnlyList<string> TokenizeOneTerm() => Tokenizer.Default.Tokenize("search");

    /// <summary>The parser alone — a quote-free query, so this is `Tokenize` plus the `ParsedQuery` record.</summary>
    [Benchmark]
    public ParsedQuery Parse() => QueryParser.Parse(Hit, Tokenizer.Default);

    /// <summary>Baseline row: identical to `SearchBenchmarks.Bm25Search`, the published ~1.3 KB.</summary>
    [Benchmark]
    public IReadOnlyList<SearchResult> SearchHitLimit10() => _bm25.Search(Hit, _limit10);

    /// <summary>
    /// The control row. Three page sizes (1, 10, 100) must fall on one line: the slope is the
    /// per-row cost of the page and the intercept is everything a query costs before it returns
    /// anything. Two points would fit a line whether the model is right or not.
    /// </summary>
    [Benchmark]
    public IReadOnlyList<SearchResult> SearchHitLimit1() => _bm25.Search(Hit, _limit1);

    /// <summary>Same query, a page of 100: the difference against `SearchHitLimit10` prices the page, per row.</summary>
    [Benchmark]
    public IReadOnlyList<SearchResult> SearchHitLimit100() => _bm25.Search(Hit, _limit100);

    /// <summary>Fixed cost: parses, plans, sizes the window, returns nothing and scores nothing.</summary>
    [Benchmark]
    public IReadOnlyList<SearchResult> SearchMissLimit10() => _bm25.Search(Miss, _limit10);

    /// <summary>The phrase path: a quoted segment declines the accumulation pass and gates per candidate.</summary>
    [Benchmark]
    public IReadOnlyList<SearchResult> SearchPhraseLimit10() => _bm25.Search(Phrase, _limit10);

    /// <summary>The same query with `ParseQuerySyntax` off: the tokenizer path, no query language.</summary>
    [Benchmark]
    public IReadOnlyList<SearchResult> SearchLiteralLimit10() => _bm25.Search(Hit, _literalLimit10);
}
