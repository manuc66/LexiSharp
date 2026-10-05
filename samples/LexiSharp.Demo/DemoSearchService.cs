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

    /// <summary>Indexes <paramref name="corpus"/> and builds the five lanes over it.</summary>
    /// <param name="corpus">
    /// The documents every lane is built over. The built-in corpus makes the demo run from a fresh
    /// clone; a BEIR corpus puts the same five lanes on data at a realistic scale.
    /// </param>
    /// <param name="segmentation">
    /// How a non-word character inside a word is treated. One tokenizer serves all five lanes on
    /// purpose: segmentation changes document frequencies, and document frequencies change every
    /// score that weights a term, so a comparison across two segmentations would not be a
    /// comparison of ranking strategies.
    /// </param>
    public DemoSearchService(DemoCorpusSet corpus, WordSegmentation segmentation)
    {
        ArgumentNullException.ThrowIfNull(corpus);

        // Stop-word removal keeps the corpus-derived associations meaningful: without it,
        // words like "the" or "with" co-occur with everything and act as noisy bridges.
        //
        // The segmentation is not cosmetic. Under Flat a separator always ends a word, so "1,000"
        // indexes as the term "000" — measured on the SciFact corpus, that term carried 19.3% of
        // the top score for the query "1,000 genomes project" — and "don't" indexes as "don".
        // The cost is not measured: enabling it takes the scan off the bulk ASCII skip.
        _tokenizer = new Tokenizer(new TokenizerOptions
        {
            RemoveStopWords = true,
            WordSegmentation = segmentation,
        });

        _spanTokenizer = _tokenizer as ISpanTokenizer;

        IReadOnlyList<SearchDocument> documents = corpus.Documents;
        var expander = PmiTermExpander.LearnFrom(documents, _tokenizer);

        _lexical = new LexiSharpIndex<SearchDocument>(options => options.Tokenizer = _tokenizer);
        _semantic = new LexiSharpIndex<SearchDocument>(options =>
        {
            options.Tokenizer = _tokenizer;
            options.TermExpander = expander;
        });

        _lexical.AddRange(documents);
        _semantic.AddRange(documents);

        // No model: the deterministic hashing provider makes the dense lane work offline.
        _dense = new InMemoryVectorSearchEngine(new HashingEmbeddingProvider(512, _tokenizer));
        _dense.Index(documents);

        _hybrid = new HybridTextSearchEngine(
            new ITextSearchEngine[] { _lexical.Engine, _semantic.Engine, _dense },
            new ReciprocalRankFusionMerger(),
            sourceNames: new[] { "lexical", "semantic", "dense" });

        _rerank = new RerankedTextSearchEngine(
            _hybrid,
            new CrossEncoderReranker(new OverlapCrossEncoder(_tokenizer)));

        WarmUp(corpus);
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

    /// <summary>Length at or below which a document is highlighted whole rather than as a window.</summary>
    private const int FullHighlightLimit = 400;

    /// <summary>
    /// The text a hit card shows, with the matched terms marked. A document short enough to read at
    /// a glance is returned whole, so every match is visible; a longer one is cut to the first
    /// window around a match, because a BEIR abstract or argument rendered in full makes a page of
    /// six hits per lane unreadable and the payload large.
    /// </summary>
    private string Highlight(string query, string text)
    {
        if (_spanTokenizer is null)
            return text;

        var terms = QueryParser.Parse(query, _tokenizer).AllTerms;

        if (terms.Count == 0)
            return text;

        if (text.Length <= FullHighlightLimit)
            return TextHighlighter.HighlightFull(text, terms, _spanTokenizer);

        var snippets = TextHighlighter.Highlight(
            text,
            terms,
            _spanTokenizer,
            new HighlightOptions { MaxSnippets = 1 });

        return snippets.Count != 0 ? snippets[0].Text : Snippet(text);
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

    /// <summary>
    /// A field's value, for the hit card. <see cref="SearchDocument.TextFields"/> is consulted as
    /// well as <see cref="SearchDocument.Fields"/>: a BEIR corpus carries its title as a text
    /// field -- the shape <c>LexiSharp.Eval.Evaluation.BuildDocuments</c> builds, and the shape the
    /// index itself reads for field-aware scoring -- so reading only <c>Fields</c> left every hit
    /// card titled with its document id, on the one corpus whose documents have titles.
    /// </summary>
    /// <remarks>
    /// The category fallback is what a corpus with no category column gets: the hit card renders
    /// that field unconditionally, so an unresolved lookup would put the document id in a slot
    /// labelled as a category.
    /// </remarks>
    private static string Field(SearchDocument document, string key)
    {
        if (document.Fields is not null && document.Fields.TryGetValue(key, out string? value))
            return value;

        if (document.TextFields is not null && document.TextFields.TryGetValue(key, out string? text))
            return text;

        return key == "category" && !string.IsNullOrEmpty(document.Category)
            ? document.Category
            : document.Id;
    }

    private static string Snippet(string text) =>
        text.Length <= 220 ? text : text[..220].TrimEnd() + "…";

    /// <summary>
    /// Runs one query through every lane before serving. The first timing a user sees is otherwise
    /// dominated by first-touch work — lazy index construction, span-tokenizer warm-up — and would
    /// report the cost of starting up as the cost of searching.
    /// </summary>
    /// <param name="corpus">The corpus being served, whose own first query is used as the seed.</param>
    private void WarmUp(DemoCorpusSet corpus)
    {
        string seed = corpus.SampleQueries.Count != 0 ? corpus.SampleQueries[0] : "refresh token";

        _lexical.Search(seed, new LexiSharpQueryOptions(Limit: 5));
        _semantic.Search(seed, new LexiSharpQueryOptions(Limit: 5));
        _dense.Search(seed, new SearchOptions(Limit: 5));
        _hybrid.SearchWithDetails(seed, new SearchOptions(Limit: 5));
        _rerank.Search(seed, new SearchOptions(Limit: 5));
    }
}
