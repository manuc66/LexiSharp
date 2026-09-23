using System.Diagnostics;
using LexiSharp;
using LexiSharp.Core;
using LexiSharp.Expansion;
using LexiSharp.Highlighting;
using LexiSharp.Hybrid;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;

namespace LexiSharp.Demo;

/// <summary>
/// Builds four retrieval strategies over the same corpus and exposes a comparison API:
/// <list type="bullet">
///   <item>lexical — plain BM25;</item>
///   <item>semantic — BM25 over an index whose documents were widened with PPMI-derived terms;</item>
///   <item>hybrid — the two engines fused by reciprocal rank fusion;</item>
///   <item>rerank — the hybrid shortlist reordered by a cross-encoder.</item>
/// </list>
/// Every lane reuses an existing LexiSharp piece; the demo only wires them together.
/// </summary>
public sealed class DemoSearchService
{
    private readonly ITokenizer _tokenizer;
    private readonly ISpanTokenizer? _spanTokenizer;
    private readonly LexiSharpIndex<SearchDocument> _lexical;
    private readonly LexiSharpIndex<SearchDocument> _semantic;
    private readonly HybridTextSearchEngine _hybrid;
    private readonly RerankedTextSearchEngine _rerank;

    public DemoSearchService()
    {
        // Stop-word removal keeps the corpus-derived associations meaningful: without it,
        // words like "the" or "with" co-occur with everything and act as noisy bridges.
        _tokenizer = new Tokenizer(new TokenizerOptions { RemoveStopWords = true });
        _spanTokenizer = _tokenizer as ISpanTokenizer;

        var corpus = DemoCorpus.Build();
        var expander = PmiTermExpander.LearnFrom(corpus, _tokenizer);

        _lexical = new LexiSharpIndex<SearchDocument>(options => options.Tokenizer = _tokenizer);
        _semantic = new LexiSharpIndex<SearchDocument>(options =>
        {
            options.Tokenizer = _tokenizer;
            options.TermExpander = expander;
        });

        _lexical.AddRange(corpus);
        _semantic.AddRange(corpus);

        _hybrid = new HybridTextSearchEngine(
            new ITextSearchEngine[] { _lexical.Engine, _semantic.Engine },
            new ReciprocalRankFusionMerger(),
            sourceNames: new[] { "lexical", "semantic" });

        _rerank = new RerankedTextSearchEngine(
            _hybrid,
            new CrossEncoderReranker(new OverlapCrossEncoder(_tokenizer)));

        WarmUp();
    }

    /// <summary>Number of indexed documents.</summary>
    public int DocumentCount => _lexical.Count;

    /// <summary>Runs the four strategies and returns their pages side by side.</summary>
    public CompareResponse Compare(string query, int limit)
    {
        var lanes = new List<LaneResult>
        {
            Measure("lexical", "Lexical · BM25", "literal term match, length-normalized", () => LexicalHits(query, limit)),
            Measure("semantic", "Semantic · BM25 + PMI expansion", "documents carry corpus-derived related terms", () => SemanticHits(query, limit)),
            Measure("hybrid", "Hybrid · RRF fusion", "lexical and semantic fused by reciprocal rank", () => HybridHits(query, limit)),
            Measure("rerank", "Hybrid + reranker", "cross-encoder reorders the hybrid shortlist", () => RerankHits(query, limit)),
        };

        return new CompareResponse(query, lanes);
    }

    /// <summary>
    /// Explains a document's rank in a lane: per-term contributions for the lexical/semantic
    /// scorers, or the per-source scores that fed the federated ranking otherwise.
    /// </summary>
    public ExplanationDto? Explain(string query, string lane, string documentId)
    {
        if (lane is "lexical" or "semantic")
        {
            var explanation = lane == "lexical"
                ? _lexical.Explain(documentId, query)
                : _semantic.Explain(documentId, query);

            return explanation is null ? null : FromTerms(explanation);
        }

        var match = _hybrid
            .SearchWithDetails(query, new SearchOptions(Limit: 50))
            .FirstOrDefault(result => string.Equals(result.DocumentId, documentId, StringComparison.Ordinal));

        if (match is null)
            return null;

        return new ExplanationDto(
            match.DocumentId,
            "sources",
            null,
            match.Score,
            null,
            null,
            Array.Empty<TermContributionDto>(),
            match.Contributions,
            new Dictionary<string, double>(StringComparer.Ordinal));
    }

    private IReadOnlyList<HitResult> LexicalHits(string query, int limit)
    {
        var hits = _lexical.Search(query, new LexiSharpQueryOptions(Limit: limit, Highlight: true));
        var results = new HitResult[hits.Count];

        for (int i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            results[i] = ToHit(hit.DocumentId, hit.Score, hit.Document, hit.HighlightedText, null);
        }

        return results;
    }

    private IReadOnlyList<HitResult> SemanticHits(string query, int limit)
    {
        var hits = _semantic.Search(query, new LexiSharpQueryOptions(Limit: limit, Highlight: true));
        var results = new HitResult[hits.Count];

        for (int i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            results[i] = ToHit(hit.DocumentId, hit.Score, hit.Document, hit.HighlightedText, null);
        }

        return results;
    }

    private IReadOnlyList<HitResult> HybridHits(string query, int limit)
    {
        var details = _hybrid.SearchWithDetails(query, new SearchOptions(Limit: limit));
        var results = new HitResult[details.Count];

        for (int i = 0; i < details.Count; i++)
        {
            var detail = details[i];
            results[i] = ToHit(detail.DocumentId, detail.Score, detail.Document, Highlight(query, detail.Document.Text), detail.Contributions);
        }

        return results;
    }

    private IReadOnlyList<HitResult> RerankHits(string query, int limit)
    {
        var hits = _rerank.Search(query, new SearchOptions(Limit: limit));
        var results = new HitResult[hits.Count];

        for (int i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            results[i] = ToHit(hit.DocumentId, hit.Score, hit.Document, Highlight(query, hit.Document.Text), null);
        }

        return results;
    }

    private static LaneResult Measure(string key, string label, string description, Func<IReadOnlyList<HitResult>> run)
    {
        var stopwatch = Stopwatch.StartNew();
        var hits = run();
        stopwatch.Stop();

        return new LaneResult(key, label, description, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), hits);
    }

    private HitResult ToHit(
        string id,
        double score,
        SearchDocument document,
        string? highlighted,
        IReadOnlyDictionary<string, double>? sources) =>
        new(
            id,
            Field(document, "title"),
            Field(document, "category"),
            score,
            Snippet(document.Text),
            highlighted ?? Snippet(document.Text),
            sources);

    private string Highlight(string query, string text)
    {
        if (_spanTokenizer is null)
            return text;

        var terms = QueryParser.Parse(query, _tokenizer).AllTerms;
        return terms.Count == 0 ? text : TextHighlighter.HighlightFull(text, terms, _spanTokenizer);
    }

    private static ExplanationDto FromTerms(ScoreExplanation explanation)
    {
        var terms = new TermContributionDto[explanation.Terms.Count];

        for (int i = 0; i < explanation.Terms.Count; i++)
        {
            var term = explanation.Terms[i];
            terms[i] = new TermContributionDto(
                term.Term,
                term.TermFrequency,
                term.DocumentFrequency,
                term.InverseDocumentFrequency,
                term.Score);
        }

        return new ExplanationDto(
            explanation.DocumentId,
            "terms",
            explanation.Algorithm,
            explanation.TotalScore,
            explanation.DocumentLength,
            explanation.AverageDocumentLength,
            terms,
            new Dictionary<string, double>(StringComparer.Ordinal),
            explanation.Parameters);
    }

    private static string Field(SearchDocument document, string key) =>
        document.Fields is not null && document.Fields.TryGetValue(key, out var value)
            ? value
            : document.Id;

    private static string Snippet(string text) =>
        text.Length <= 220 ? text : text[..220].TrimEnd() + "…";

    private void WarmUp()
    {
        const string seed = "refresh token";

        _lexical.Search(seed, new LexiSharpQueryOptions(Limit: 5));
        _semantic.Search(seed, new LexiSharpQueryOptions(Limit: 5));
        _hybrid.SearchWithDetails(seed, new SearchOptions(Limit: 5));
        _rerank.Search(seed, new SearchOptions(Limit: 5));
    }
}
