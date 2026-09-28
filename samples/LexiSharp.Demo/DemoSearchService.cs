using System.Diagnostics;
using LexiSharp;
using LexiSharp.Core;
using LexiSharp.Embeddings;
using LexiSharp.Expansion;
using LexiSharp.Highlighting;
using LexiSharp.Hybrid;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;

namespace LexiSharp.Demo;

/// <summary>
/// Builds five retrieval strategies over the same corpus and exposes a comparison API:
/// <list type="bullet">
///   <item>lexical — plain BM25;</item>
///   <item>semantic — BM25 over an index whose documents were widened with PPMI-derived terms;</item>
///   <item>dense — cosine similarity over deterministic hashing embeddings;</item>
///   <item>hybrid — lexical, semantic and dense fused by reciprocal rank fusion;</item>
///   <item>rerank — the hybrid shortlist reordered by a query-term-overlap scorer.</item>
/// </list>
/// Every lane reuses an existing LexiSharp piece; the demo only wires them together.
/// </summary>
public sealed class DemoSearchService
{
    private readonly ITokenizer _tokenizer;
    private readonly ISpanTokenizer? _spanTokenizer;
    private readonly LexiSharpIndex<SearchDocument> _lexical;
    private readonly LexiSharpIndex<SearchDocument> _semantic;
    private readonly InMemoryVectorSearchEngine _dense;
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

        // No model: the deterministic hashing provider makes the dense lane work offline.
        _dense = new InMemoryVectorSearchEngine(new HashingEmbeddingProvider(512, _tokenizer));
        _dense.Index(corpus);

        _hybrid = new HybridTextSearchEngine(
            new ITextSearchEngine[] { _lexical.Engine, _semantic.Engine, _dense },
            new ReciprocalRankFusionMerger(),
            sourceNames: new[] { "lexical", "semantic", "dense" });

        _rerank = new RerankedTextSearchEngine(
            _hybrid,
            new CrossEncoderReranker(new OverlapCrossEncoder(_tokenizer)));

        WarmUp();
    }

    /// <summary>Number of indexed documents.</summary>
    public int DocumentCount => _lexical.Count;

    /// <summary>Runs the five strategies and returns their pages side by side.</summary>
    public CompareResponse Compare(string query, int limit)
    {
        var lanes = new List<LaneResult>
        {
            Measure("lexical", "Lexical · BM25", "literal term match, length-normalized", () => LexicalHits(query, limit)),
            Measure("semantic", "Semantic · BM25 + PMI expansion", "documents carry corpus-derived related terms", () => SemanticHits(query, limit)),
            Measure("dense", "Dense · hashing embedding", "cosine over deterministic hashed vectors", () => DenseHits(query, limit)),
            Measure("hybrid", "Hybrid · RRF fusion", "lexical, semantic and dense fused by rank", () => HybridHits(query, limit)),
            Measure("rerank", "Hybrid + reranker", "query-term overlap reorders the hybrid shortlist", () => RerankHits(query, limit)),
        };

        return new CompareResponse(query, lanes);
    }

    /// <summary>
    /// Explains a document's rank in a lane. Every lane additionally returns the full
    /// <see cref="SearchTrace"/> chain for that document — the stages it passed through and the
    /// score going in and out of each — which is what makes a reranked or boosted rank explicable
    /// at all. The per-term breakdown is still filled in for the lanes whose scorer implements
    /// <see cref="IScoreExplainer"/>.
    /// </summary>
    public ExplanationDto? Explain(string query, string lane, string documentId)
    {
        var stages = TraceChain(query, lane, documentId);
        var agreement = Agreement(query, documentId);

        if (lane is "lexical" or "semantic")
        {
            var explanation = lane == "lexical"
                ? _lexical.Explain(documentId, query)
                : _semantic.Explain(documentId, query);

            if (explanation is null)
                return stages.Count == 0 ? null : new ExplanationDto(
                    documentId, "stages", null, FinalScore(stages), null, null,
                    Array.Empty<TermContributionDto>(),
                    new Dictionary<string, double>(StringComparer.Ordinal),
                    new Dictionary<string, double>(StringComparer.Ordinal),
                    stages, agreement);

            return FromTerms(explanation, stages, agreement);
        }

        if (lane == "dense")
        {
            var dense = _dense
                .Search(query, new SearchOptions(Limit: 50))
                .FirstOrDefault(result => string.Equals(result.DocumentId, documentId, StringComparison.Ordinal));

            if (dense is null)
                return null;

            return new ExplanationDto(
                dense.DocumentId,
                "sources",
                null,
                dense.Score,
                null,
                null,
                Array.Empty<TermContributionDto>(),
                new Dictionary<string, double>(StringComparer.Ordinal) { ["dense"] = dense.Score },
                new Dictionary<string, double>(StringComparer.Ordinal),
                stages, agreement);
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
            new Dictionary<string, double>(StringComparer.Ordinal),
            stages, agreement);
    }

    /// <summary>
    /// Classifies the document against the demo's three federated sources, so the panel can say
    /// whether it is in the page because everything agreed or because one lane insisted. Null when
    /// the document is not on the fused page at all - there is nothing to compare.
    /// </summary>
    private AgreementDto? Agreement(string query, string documentId)
    {
        var page = _hybrid.SearchWithDetails(query, new SearchOptions(Limit: 20));
        var reports = RetrievalAgreementAnalyzer.Analyze(page, sourceNames: ["lexical", "semantic", "dense"]);

        var report = reports.FirstOrDefault(entry =>
            string.Equals(entry.DocumentId, documentId, StringComparison.Ordinal));

        return report is null ? null : new AgreementDto(
            ToLabel(report.Agreement),
            report.Strengths,
            report.StrongSources,
            report.AbsentSources);
    }

    /// <summary>Lowercase name for the UI; the enum is structural so the labels stay camelCase.</summary>
    private static string ToLabel(RetrievalAgreement agreement) => agreement switch
    {
        RetrievalAgreement.None => "none",
        RetrievalAgreement.SingleSource => "singleSource",
        RetrievalAgreement.Lukewarm => "lukewarm",
        RetrievalAgreement.Disputed => "disputed",
        RetrievalAgreement.Unanimous => "unanimous",
        _ => agreement.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Replays a lane's search with a trace attached and returns the recorded steps for one
    /// document, in pipeline order. The search is deterministic, so replaying it yields the same
    /// ranking the user just clicked on.
    /// </summary>
    private IReadOnlyList<TraceStageDto> TraceChain(string query, string lane, string documentId)
    {
        // A page deep enough to contain the document the user clicked, so a document ranked below
        // the visible page is still explainable.
        var trace = new SearchTrace(capacity: 128);

        switch (lane)
        {
            case "lexical":
                _lexical.Search(query, new LexiSharpQueryOptions(Limit: 50, Trace: trace));
                break;
            case "semantic":
                _semantic.Search(query, new LexiSharpQueryOptions(Limit: 50, Trace: trace));
                break;
            case "dense":
                _dense.Search(query, new SearchOptions(Limit: 50, Trace: trace));
                break;
            case "hybrid":
                _hybrid.SearchWithDetails(query, new SearchOptions(Limit: 50, Trace: trace));
                break;
            case "rerank":
                _rerank.Search(query, new SearchOptions(Limit: 50, Trace: trace));
                break;
            default:
                return Array.Empty<TraceStageDto>();
        }

        var stages = new List<TraceStageDto>();

        foreach (var step in trace.Steps)
        {
            // The route decision is per query, not per document: keep it on every chain so the
            // panel can show which lane path was taken.
            bool keep = step.Stage == TraceStage.Route
                        || string.Equals(step.DocumentId, documentId, StringComparison.Ordinal);

            if (keep)
            {
                stages.Add(new TraceStageDto(
                    step.Stage.ToString().ToLowerInvariant(),
                    step.DocumentId,
                    step.Before,
                    step.After,
                    step.Detail));
            }
        }

        return stages;
    }

    /// <summary>The score the chain ended on: the last recorded after-score that is a number.</summary>
    private static double FinalScore(IReadOnlyList<TraceStageDto> stages)
    {
        for (int i = stages.Count - 1; i >= 0; i--)
        {
            if (!double.IsNaN(stages[i].After))
                return stages[i].After;
        }

        return 0;
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

    private IReadOnlyList<HitResult> DenseHits(string query, int limit)
    {
        var hits = _dense.Search(query, new SearchOptions(Limit: limit));
        var results = new HitResult[hits.Count];

        for (int i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            results[i] = ToHit(hit.DocumentId, hit.Score, hit.Document, Highlight(query, hit.Document.Text), null);
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

    private static ExplanationDto FromTerms(
        ScoreExplanation explanation,
        IReadOnlyList<TraceStageDto> stages,
        AgreementDto? agreement)
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
            explanation.Parameters,
            stages,
            agreement);
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
        _dense.Search(seed, new SearchOptions(Limit: 5));
        _hybrid.SearchWithDetails(seed, new SearchOptions(Limit: 5));
        _rerank.Search(seed, new SearchOptions(Limit: 5));
    }
}
