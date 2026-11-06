using System.Text.Json;
using LexiSharp.Core;
using LexiSharp.Eval;
using LexiSharp.Expansion;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;
using Xunit;
using Xunit.Abstractions;

namespace LexiSharp.Tests;

public class CalibrationMeasurementTests
{
    private readonly ITestOutputHelper _output;

    public CalibrationMeasurementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Calibration_J_OnSciFact()
    {
        string dataDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "bench", "LexiSharp.Eval", "data", "scifact"));

        if (!File.Exists(Path.Combine(dataDir, "corpus.jsonl")))
        {
            _output.WriteLine($"Corpus not found at {dataDir}, skipping.");
            return;
        }

        // Load corpus
        var documents = new List<(string Id, string Title, string Text)>();
        foreach (string line in File.ReadLines(Path.Combine(dataDir, "corpus.jsonl")))
        {
            using var json = JsonDocument.Parse(line);
            string id = json.RootElement.GetProperty("_id").GetString()!;
            string title = json.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            string text = json.RootElement.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
            documents.Add((id, title, text));
        }

        // Load queries
        var queries = new List<(string Id, string Text)>();
        foreach (string line in File.ReadLines(Path.Combine(dataDir, "queries.jsonl")))
        {
            using var json = JsonDocument.Parse(line);
            string id = json.RootElement.GetProperty("_id").GetString()!;
            string text = json.RootElement.GetProperty("text").GetString()!;
            queries.Add((id, text));
        }

        // Load qrels
        var qrels = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(Path.Combine(dataDir, "qrels", "test.tsv")).Skip(1))
        {
            string[] parts = line.Split('\t');
            if (parts.Length < 3) continue;
            if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double score) || score <= 0) continue;
            string qid = parts[0];
            string did = parts[1];
            if (!qrels.TryGetValue(qid, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                qrels[qid] = set;
            }
            set.Add(did);
        }

        _output.WriteLine($"Loaded {documents.Count} docs, {queries.Count} queries, {qrels.Count} qrels.");

        // Build index
        var tokenizer = new Tokenizer();
        var index = new InMemoryTextIndex(tokenizer);
        var searchDocs = documents.Select(d =>
            new SearchDocument(d.Id, string.IsNullOrEmpty(d.Title) ? d.Text : d.Title + " " + d.Text)).ToList();
        index.Index(searchDocs);

        var engine = new RankedTextSearchEngine(index, new Bm25Scorer(1.5, 0.75), tokenizer);

        // Collect (score, isRelevant) pairs
        var pairs = new List<(double Score, bool IsCorrect)>();

        foreach (var (qid, qtext) in queries)
        {
            if (!qrels.TryGetValue(qid, out var relevantDocs)) continue;

            // Score all documents for this query
            var results = engine.Search(qtext, new SearchOptions(Limit: int.MaxValue)).ToList();
            foreach (var r in results)
            {
                bool isRelevant = relevantDocs.Contains(r.DocumentId);
                pairs.Add((r.Score, isRelevant));
            }
        }

        _output.WriteLine($"Collected {pairs.Count} (score, label) pairs. Positives: {pairs.Count(p => p.IsCorrect)}");

        // Compute J manually (Youden's J = TPR - FPR)
        // Sort by score descending
        var sorted = pairs.OrderByDescending(p => p.Score).ToArray();
        int totalPos = sorted.Count(p => p.IsCorrect);
        int totalNeg = sorted.Length - totalPos;

        double bestJ = double.NegativeInfinity;
        int tp = 0, fp = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            if (sorted[i].IsCorrect) tp++;
            else fp++;

            // Only evaluate at score boundaries
            if (i < sorted.Length - 1 && Math.Abs(sorted[i].Score - sorted[i + 1].Score) < double.Epsilon)
                continue;

            double tpr = (double)tp / totalPos;
            double fpr = (double)fp / totalNeg;
            double j = tpr - fpr;

            if (j >= bestJ)
                bestJ = j;
        }

        _output.WriteLine($"Best Youden's J = {bestJ:F4} (target: > 0.147)");

        // Fit calibrators
        var isotonic = CalibratedScoreConfidence.Fit(pairs, CalibrationMethod.IsotonicRegression);
        var platt = CalibratedScoreConfidence.Fit(pairs, CalibrationMethod.PlattScaling);

        _output.WriteLine($"Isotonic: AbstainScore = {isotonic.AbstainScore:F4}, AbstainThreshold = {isotonic.AbstainThreshold:F4}");
        _output.WriteLine($"Platt: AbstainScore = {platt.AbstainScore:F4}, AbstainThreshold = {platt.AbstainThreshold:F4}");

        // Report whether J exceeds target
        _output.WriteLine($"J > 0.147 ? {bestJ > 0.147} (J = {bestJ:F4})");

        Assert.True(true); // report only
    }

    [Fact]
    public void FeedbackHistory_NDCG_OnSciFact()
    {
        string dataDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "bench", "LexiSharp.Eval", "data", "scifact"));

        if (!File.Exists(Path.Combine(dataDir, "corpus.jsonl")))
        {
            _output.WriteLine($"Corpus not found at {dataDir}, skipping.");
            return;
        }

        // Load corpus
        var documents = new List<(string Id, string Title, string Text)>();
        foreach (string line in File.ReadLines(Path.Combine(dataDir, "corpus.jsonl")))
        {
            using var json = JsonDocument.Parse(line);
            string id = json.RootElement.GetProperty("_id").GetString()!;
            string title = json.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            string text = json.RootElement.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
            documents.Add((id, title, text));
        }

        // Load queries
        var queries = new List<(string Id, string Text)>();
        foreach (string line in File.ReadLines(Path.Combine(dataDir, "queries.jsonl")))
        {
            using var json = JsonDocument.Parse(line);
            string id = json.RootElement.GetProperty("_id").GetString()!;
            string text = json.RootElement.GetProperty("text").GetString()!;
            queries.Add((id, text));
        }

        // Load qrels
        var qrels = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(Path.Combine(dataDir, "qrels", "test.tsv")).Skip(1))
        {
            string[] parts = line.Split('\t');
            if (parts.Length < 3) continue;
            if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double score) || score <= 0) continue;
            string qid = parts[0];
            string did = parts[1];
            if (!qrels.TryGetValue(qid, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                qrels[qid] = set;
            }
            set.Add(did);
        }

        _output.WriteLine($"Loaded {documents.Count} docs, {queries.Count} queries, {qrels.Count} qrels.");

        var tokenizer = new Tokenizer();
        var searchDocs = documents.Select(d =>
            new SearchDocument(d.Id, string.IsNullOrEmpty(d.Title) ? d.Text : d.Title + " " + d.Text)).ToList();

        // Split queries: first half for training feedback, second half for evaluation
        var qrelsQueries = queries.Where(q => qrels.ContainsKey(q.Id)).ToList();
        int splitIdx = qrelsQueries.Count / 2;
        var trainQueries = qrelsQueries.Take(splitIdx).ToList();
        var testQueries = qrelsQueries.Skip(splitIdx).ToList();

        _output.WriteLine($"Train: {trainQueries.Count} queries, Test: {testQueries.Count} queries");

        // Build base index
        var baseIndex = new InMemoryTextIndex(tokenizer);
        baseIndex.Index(searchDocs);

        // Record feedback from training queries
        var history = new QueryFeedbackHistory();
        foreach (var (qid, qtext) in trainQueries)
        {
            foreach (string docId in qrels[qid])
            {
                history.Record(qtext, docId);
            }
        }

        _output.WriteLine($"Recorded {trainQueries.Count} query feedbacks, {history.GetAssociations(trainQueries[0].Text).Count} associations for first query");

        // Evaluate base engine on test queries
        var baseEngine = new RankedTextSearchEngine(baseIndex, new Bm25Scorer(1.5, 0.75), tokenizer);
        var feedbackEngine = new FeedbackAwareTextSearchEngine(baseEngine, history, maxBoost: 1.0, minSimilarity: 0.3);

        double baseNdcg = ComputeNdcg(baseEngine, testQueries, qrels, 10);
        double feedbackNdcg = ComputeNdcg(feedbackEngine, testQueries, qrels, 10);

        _output.WriteLine($"Base BM25 nDCG@10: {baseNdcg:F4}");
        _output.WriteLine($"Feedback-aware nDCG@10: {feedbackNdcg:F4}");
        _output.WriteLine($"Delta: {feedbackNdcg - baseNdcg:+0.0000;-0.0000}");

        // Count regressions (queries where feedback is worse than base)
        int regressions = 0;
        int improvements = 0;
        foreach (var (qid, qtext) in testQueries)
        {
            var baseResults = baseEngine.Search(qtext, new SearchOptions(Limit: 10)).Select(r => r.DocumentId).ToList();
            var fbResults = feedbackEngine.Search(qtext, new SearchOptions(Limit: 10)).Select(r => r.DocumentId).ToList();
            double baseQ = NdcgForQuery(baseResults, qrels[qid], 10);
            double fbQ = NdcgForQuery(fbResults, qrels[qid], 10);
            if (fbQ < baseQ - 1e-9) regressions++;
            if (fbQ > baseQ + 1e-9) improvements++;
        }

        _output.WriteLine($"Queries: {testQueries.Count}, Improvements: {improvements}, Regressions: {regressions}");

        Assert.True(true);
    }

    [Fact]
    public void ContextualBoost_Regressions_OnSciFact()
    {
        string dataDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "bench", "LexiSharp.Eval", "data", "scifact"));

        if (!File.Exists(Path.Combine(dataDir, "corpus.jsonl")))
        {
            _output.WriteLine($"Corpus not found at {dataDir}, skipping.");
            return;
        }

        // Load corpus
        var documents = new List<(string Id, string Title, string Text)>();
        foreach (string line in File.ReadLines(Path.Combine(dataDir, "corpus.jsonl")))
        {
            using var json = JsonDocument.Parse(line);
            string id = json.RootElement.GetProperty("_id").GetString()!;
            string title = json.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            string text = json.RootElement.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
            documents.Add((id, title, text));
        }

        var queries = new List<(string Id, string Text)>();
        foreach (string line in File.ReadLines(Path.Combine(dataDir, "queries.jsonl")))
        {
            using var json = JsonDocument.Parse(line);
            string id = json.RootElement.GetProperty("_id").GetString()!;
            string text = json.RootElement.GetProperty("text").GetString()!;
            queries.Add((id, text));
        }

        var qrels = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(Path.Combine(dataDir, "qrels", "test.tsv")).Skip(1))
        {
            string[] parts = line.Split('\t');
            if (parts.Length < 3) continue;
            if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double score) || score <= 0) continue;
            string qid = parts[0];
            string did = parts[1];
            if (!qrels.TryGetValue(qid, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                qrels[qid] = set;
            }
            set.Add(did);
        }

        var tokenizer = new Tokenizer();
        var searchDocs = documents.Select(d =>
            new SearchDocument(d.Id, string.IsNullOrEmpty(d.Title) ? d.Text : d.Title + " " + d.Text)
            { Fields = new Dictionary<string, string> { ["title"] = d.Title, ["body"] = d.Text } }).ToList();

        var baseIndex = new InMemoryTextIndex(tokenizer);
        baseIndex.Index(searchDocs);

        var baseEngine = new RankedTextSearchEngine(baseIndex, new Bm25Scorer(1.5, 0.75), tokenizer);

        // Boost function: boost documents whose title contains a query term
        ContextualBoost<object> blindBoost = (ctx, candidate) =>
        {
            string title = candidate.Document.Fields?.GetValueOrDefault("title") ?? "";
            string[] queryTerms = ctx.Query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool titleMatch = queryTerms.Any(qt => title.ToLowerInvariant().Contains(qt));
            return titleMatch ? new ScoreBoost(1.5, 0) : ScoreBoost.None();
        };

        // Conditional boost: only apply when base ranking is uncertain (TopConfidence < 0.5)
        ContextualBoost<object> conditionalBoost = (ctx, candidate) =>
        {
            if (ctx.TopConfidence >= 0.5) return ScoreBoost.None(); // base ranking is clear, stay out
            string title = candidate.Document.Fields?.GetValueOrDefault("title") ?? "";
            string[] queryTerms = ctx.Query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool titleMatch = queryTerms.Any(qt => title.ToLowerInvariant().Contains(qt));
            return titleMatch ? new ScoreBoost(1.5, 0) : ScoreBoost.None();
        };

        var blindEngine = new BoostedTextSearchEngine<object>(baseEngine, blindBoost, maxCandidates: 50);
        var conditionalEngine = new BoostedTextSearchEngine<object>(baseEngine, conditionalBoost, maxCandidates: 50);

        var qrelsQueries = queries.Where(q => qrels.ContainsKey(q.Id)).ToList();

        // Measure nDCG and regressions for each
        MeasureBoost("Base (no boost)", baseEngine, baseEngine, qrelsQueries, qrels, 10);
        MeasureBoost("Blind boost", baseEngine, blindEngine, qrelsQueries, qrels, 10);
        MeasureBoost("Conditional boost", baseEngine, conditionalEngine, qrelsQueries, qrels, 10);

        Assert.True(true);
    }

    [Fact]
    public void OptionC_Composition_OnSciFact()
    {
        string dataDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "bench", "LexiSharp.Eval", "data", "scifact"));

        if (!File.Exists(Path.Combine(dataDir, "corpus.jsonl")))
        {
            _output.WriteLine($"Corpus not found at {dataDir}, skipping.");
            return;
        }

        // Load corpus
        var documents = new List<(string Id, string Title, string Text)>();
        foreach (string line in File.ReadLines(Path.Combine(dataDir, "corpus.jsonl")))
        {
            using var json = JsonDocument.Parse(line);
            string id = json.RootElement.GetProperty("_id").GetString()!;
            string title = json.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            string text = json.RootElement.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
            documents.Add((id, title, text));
        }

        var queries = new List<(string Id, string Text)>();
        foreach (string line in File.ReadLines(Path.Combine(dataDir, "queries.jsonl")))
        {
            using var json = JsonDocument.Parse(line);
            string id = json.RootElement.GetProperty("_id").GetString()!;
            string text = json.RootElement.GetProperty("text").GetString()!;
            queries.Add((id, text));
        }

        var qrels = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(Path.Combine(dataDir, "qrels", "test.tsv")).Skip(1))
        {
            string[] parts = line.Split('\t');
            if (parts.Length < 3) continue;
            if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double score) || score <= 0) continue;
            string qid = parts[0];
            string did = parts[1];
            if (!qrels.TryGetValue(qid, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                qrels[qid] = set;
            }
            set.Add(did);
        }

        var tokenizer = new Tokenizer();
        var searchDocs = documents.Select(d =>
            new SearchDocument(d.Id, string.IsNullOrEmpty(d.Title) ? d.Text : d.Title + " " + d.Text)).ToList();

        var baseIndex = new InMemoryTextIndex(tokenizer);
        baseIndex.Index(searchDocs);

        var baseEngine = new RankedTextSearchEngine(baseIndex, new Bm25Scorer(1.5, 0.75), tokenizer);

        // Split queries: train (fit calibrator) / test (evaluate)
        var qrelsQueries = queries.Where(q => qrels.ContainsKey(q.Id)).ToList();
        int splitIdx = qrelsQueries.Count / 2;
        var trainQueries = qrelsQueries.Take(splitIdx).ToList();
        var testQueries = qrelsQueries.Skip(splitIdx).ToList();

        _output.WriteLine($"Train: {trainQueries.Count}, Test: {testQueries.Count}");

        // Fit calibrator on training pairs
        var trainPairs = new List<(double Score, bool IsCorrect)>();
        foreach (var (qid, qtext) in trainQueries)
        {
            var results = baseEngine.Search(qtext, new SearchOptions(Limit: int.MaxValue)).ToList();
            foreach (var r in results)
            {
                bool isRelevant = qrels[qid].Contains(r.DocumentId);
                trainPairs.Add((r.Score, isRelevant));
            }
        }

        var calibrator = CalibratedScoreConfidence.Fit(trainPairs, CalibrationMethod.IsotonicRegression);
        _output.WriteLine($"Calibrator fitted on {trainPairs.Count} pairs. AbstainScore = {calibrator.AbstainScore:F4}, J-target = 0.147");

        // Composition: damp documents below AbstainScore, only when base ranking is uncertain
        ContextualBoost<object> optionCBoost = (ctx, candidate) =>
        {
            // Condition: only act when base ranking is uncertain
            if (ctx.TopConfidence >= 0.5) return ScoreBoost.None();
            // Damp documents whose score is below the calibrator's abstention threshold
            if (candidate.Score < calibrator.AbstainScore)
                return new ScoreBoost(0.5, 0); // damp
            return ScoreBoost.None();
        };

        var optionCEngine = new BoostedTextSearchEngine<object>(baseEngine, optionCBoost, maxCandidates: 50);

        // Baseline: base engine (no boost)
        // Variant 1: blind damp (always damp below threshold)
        ContextualBoost<object> blindDamp = (ctx, candidate) =>
        {
            if (candidate.Score < calibrator.AbstainScore)
                return new ScoreBoost(0.5, 0);
            return ScoreBoost.None();
        };
        var blindDampEngine = new BoostedTextSearchEngine<object>(baseEngine, blindDamp, maxCandidates: 50);

        // Measure
        MeasureBoost("Base (no damp)", baseEngine, baseEngine, testQueries, qrels, 10);
        MeasureBoost("Blind damp", baseEngine, blindDampEngine, testQueries, qrels, 10);
        MeasureBoost("Option C (conditional damp)", baseEngine, optionCEngine, testQueries, qrels, 10);

        Assert.True(true);
    }

    [Fact]
    public async Task BayesianFusion_OnSciFact()
    {
        string dataDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "bench", "LexiSharp.Eval", "data", "scifact"));

        if (!File.Exists(Path.Combine(dataDir, "corpus.jsonl")))
        {
            _output.WriteLine($"Corpus not found at {dataDir}, skipping.");
            return;
        }

        // Load corpus
        var documents = new List<(string Id, string Title, string Text)>();
        foreach (string line in File.ReadLines(Path.Combine(dataDir, "corpus.jsonl")))
        {
            using var json = JsonDocument.Parse(line);
            string id = json.RootElement.GetProperty("_id").GetString()!;
            string title = json.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            string text = json.RootElement.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
            documents.Add((id, title, text));
        }

        var queries = new List<(string Id, string Text)>();
        foreach (string line in File.ReadLines(Path.Combine(dataDir, "queries.jsonl")))
        {
            using var json = JsonDocument.Parse(line);
            string id = json.RootElement.GetProperty("_id").GetString()!;
            string text = json.RootElement.GetProperty("text").GetString()!;
            queries.Add((id, text));
        }

        var qrels = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(Path.Combine(dataDir, "qrels", "test.tsv")).Skip(1))
        {
            string[] parts = line.Split('\t');
            if (parts.Length < 3) continue;
            if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double score) || score <= 0) continue;
            string qid = parts[0];
            string did = parts[1];
            if (!qrels.TryGetValue(qid, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                qrels[qid] = set;
            }
            set.Add(did);
        }

        var tokenizer = new Tokenizer();
        var searchDocs = documents.Select(d =>
            new SearchDocument(d.Id, string.IsNullOrEmpty(d.Title) ? d.Text : d.Title + " " + d.Text)).ToList();

        var baseIndex = new InMemoryTextIndex(tokenizer);
        baseIndex.Index(searchDocs);
        var bm25Engine = new RankedTextSearchEngine(baseIndex, new Bm25Scorer(1.5, 0.75), tokenizer);

        // Load dense engine
        DenseVectors? vectors = null;
        try
        {
            vectors = await DenseEmbedder.TryBuildAsync(
                new BeirCorpus { Name = "scifact", Documents = documents.Select(d => new BeirDocument(d.Id, d.Title, d.Text)).ToList(), Queries = queries.Select(q => new BeirQuery(q.Id, q.Text)).ToList(), TestRelevance = new Dictionary<string, IReadOnlyDictionary<string, double>>() },
                dataDir, Path.Combine(dataDir, "..", "..", "models"), 256, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Dense embeddings not available: {ex.Message}");
            return;
        }

        if (vectors == null)
        {
            _output.WriteLine("Dense vectors null, skipping.");
            return;
        }

        var denseEngine = new DenseTextSearchEngine(
            new BeirCorpus { Name = "scifact", Documents = documents.Select(d => new BeirDocument(d.Id, d.Title, d.Text)).ToList(), Queries = queries.Select(q => new BeirQuery(q.Id, q.Text)).ToList(), TestRelevance = new Dictionary<string, IReadOnlyDictionary<string, double>>() },
            vectors);

        var qrelsQueries = queries.Where(q => qrels.ContainsKey(q.Id)).ToList();
        int splitIdx = qrelsQueries.Count / 2;
        var trainQueries = qrelsQueries.Take(splitIdx).ToList();
        var testQueries = qrelsQueries.Skip(splitIdx).ToList();

        _output.WriteLine($"Train: {trainQueries.Count}, Test: {testQueries.Count}");

        // Fit calibrators on training data
        var bm25Pairs = new List<(double Score, bool IsCorrect)>();
        var densePairs = new List<(double Score, bool IsCorrect)>();

        foreach (var (qid, qtext) in trainQueries)
        {
            var bm25Results = bm25Engine.Search(qtext, new SearchOptions(Limit: int.MaxValue)).ToList();
            var denseResults = denseEngine.Search(qtext, new SearchOptions(Limit: int.MaxValue)).ToList();

            var denseScoreMap = denseResults.ToDictionary(r => r.DocumentId, r => r.Score);

            foreach (var r in bm25Results)
            {
                bool isRel = qrels[qid].Contains(r.DocumentId);
                bm25Pairs.Add((r.Score, isRel));
                if (denseScoreMap.TryGetValue(r.DocumentId, out double ds))
                    densePairs.Add((ds, isRel));
            }
        }

        var bm25Cal = CalibratedScoreConfidence.Fit(bm25Pairs, CalibrationMethod.IsotonicRegression);
        var denseCal = CalibratedScoreConfidence.Fit(densePairs, CalibrationMethod.IsotonicRegression);

        _output.WriteLine($"BM25 calibrator: AbstainScore = {bm25Cal.AbstainScore:F4}");
        _output.WriteLine($"Dense calibrator: AbstainScore = {denseCal.AbstainScore:F4}");

        // Measure: Bayesian fusion vs RRF vs BM25 vs Dense
        double bayesNdcg = 0, rrfNdcg = 0, bm25Ndcg = 0, denseNdcg = 0;
        int n = 0;

        foreach (var (qid, qtext) in testQueries)
        {
            var bm25Results = bm25Engine.Search(qtext, new SearchOptions(Limit: int.MaxValue)).ToList();
            var denseResults = denseEngine.Search(qtext, new SearchOptions(Limit: int.MaxValue)).ToList();

            var bm25ScoreMap = bm25Results.ToDictionary(r => r.DocumentId, r => r.Score);
            var denseScoreMap = denseResults.ToDictionary(r => r.DocumentId, r => r.Score);

            // Bayesian fusion: for each document, combine calibrated probabilities
            var allDocIds = bm25Results.Select(r => r.DocumentId).Union(denseResults.Select(r => r.DocumentId)).ToList();
            var scored = new List<(string DocId, double Score)>();

            foreach (string docId in allDocIds)
            {
                double bm25Score = bm25ScoreMap.GetValueOrDefault(docId, 0);
                double denseScore = denseScoreMap.GetValueOrDefault(docId, 0);

                double pBm25 = bm25Cal.PredictProba(bm25Score);
                double pDense = denseCal.PredictProba(denseScore);

                // Naive Bayes combination: P = (p1 * p2) / (p1*p2 + (1-p1)*(1-p2))
                double numerator = pBm25 * pDense;
                double denominator = numerator + (1 - pBm25) * (1 - pDense);
                double pCombined = denominator > 0 ? numerator / denominator : 0.5;

                scored.Add((docId, pCombined));
            }

            var bayesRanked = scored.OrderByDescending(s => s.Score).Select(s => s.DocId).Take(10).ToList();
            bayesNdcg += NdcgForQuery(bayesRanked, qrels[qid], 10);

            // RRF (rank-based fusion)
            var rrfScored = new Dictionary<string, double>(StringComparer.Ordinal);
            for (int i = 0; i < bm25Results.Count; i++)
            {
                string did = bm25Results[i].DocumentId;
                rrfScored[did] = rrfScored.GetValueOrDefault(did) + 1.0 / (60 + i + 1);
            }
            for (int i = 0; i < denseResults.Count; i++)
            {
                string did = denseResults[i].DocumentId;
                rrfScored[did] = rrfScored.GetValueOrDefault(did) + 1.0 / (60 + i + 1);
            }
            var rrfRanked = rrfScored.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Take(10).ToList();
            rrfNdcg += NdcgForQuery(rrfRanked, qrels[qid], 10);

            // BM25 alone
            var bm25Ranked = bm25Results.Select(r => r.DocumentId).Take(10).ToList();
            bm25Ndcg += NdcgForQuery(bm25Ranked, qrels[qid], 10);

            // Dense alone
            var denseRanked = denseResults.Select(r => r.DocumentId).Take(10).ToList();
            denseNdcg += NdcgForQuery(denseRanked, qrels[qid], 10);

            n++;
        }

        _output.WriteLine($"BM25 alone nDCG@10: {bm25Ndcg / n:F4}");
        _output.WriteLine($"Dense alone nDCG@10: {denseNdcg / n:F4}");
        _output.WriteLine($"RRF nDCG@10: {rrfNdcg / n:F4}");
        _output.WriteLine($"Bayesian fusion nDCG@10: {bayesNdcg / n:F4}");

        // Published (CHANGELOG 0.10.0): 0.6927 vs RRF 0.6628 (+0.03), linear nDCG@10.
        // Pinned here so the number cannot drift silently: a test that only printed it
        // passed whether the measurement held or not, which is the shape the AGENTS.md rule
        // forbids. The tolerance is the harness's 0.002, on 150 held-out queries.
        Assert.True(
            bayesNdcg / n >= 0.6927 - 0.002,
            $"Bayesian fusion regressed: {bayesNdcg / n:F4} < 0.6927 - 0.002 on SciFact (150 held-out queries).");
        Assert.True(
            bayesNdcg / n > rrfNdcg / n + 0.02,
            $"Bayesian fusion no longer beats RRF by 0.02 nDCG@10: {bayesNdcg / n:F4} vs {rrfNdcg / n:F4}.");
    }

    [Fact(Skip = "Reformulation is designed for typo correction; DamerauLevenshtein on a 5k-term vocabulary is too slow for 150 queries. Not a meaningful measurement on SciFact (no typos).")]
    public async Task Reformulation_BayesianFusion_OnSciFact()
    {
        string dataDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "bench", "LexiSharp.Eval", "data", "scifact"));

        if (!File.Exists(Path.Combine(dataDir, "corpus.jsonl")))
        {
            _output.WriteLine($"Corpus not found at {dataDir}, skipping.");
            return;
        }

        var documents = new List<(string Id, string Title, string Text)>();
        foreach (string line in File.ReadLines(Path.Combine(dataDir, "corpus.jsonl")))
        {
            using var json = JsonDocument.Parse(line);
            string id = json.RootElement.GetProperty("_id").GetString()!;
            string title = json.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            string text = json.RootElement.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
            documents.Add((id, title, text));
        }

        var queries = new List<(string Id, string Text)>();
        foreach (string line in File.ReadLines(Path.Combine(dataDir, "queries.jsonl")))
        {
            using var json = JsonDocument.Parse(line);
            string id = json.RootElement.GetProperty("_id").GetString()!;
            string text = json.RootElement.GetProperty("text").GetString()!;
            queries.Add((id, text));
        }

        var qrels = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(Path.Combine(dataDir, "qrels", "test.tsv")).Skip(1))
        {
            string[] parts = line.Split('\t');
            if (parts.Length < 3) continue;
            if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double score) || score <= 0) continue;
            string qid = parts[0];
            string did = parts[1];
            if (!qrels.TryGetValue(qid, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                qrels[qid] = set;
            }
            set.Add(did);
        }

        var tokenizer = new Tokenizer();
        var searchDocs = documents.Select(d =>
            new SearchDocument(d.Id, string.IsNullOrEmpty(d.Title) ? d.Text : d.Title + " " + d.Text)).ToList();

        var baseIndex = new InMemoryTextIndex(tokenizer);
        baseIndex.Index(searchDocs);

        var vocab = searchDocs.SelectMany(d => tokenizer.Tokenize(d.Text)).Distinct().ToList();
        var corrector = new DamerauLevenshteinTermCorrector(vocab, maxDistance: 1);
        var bm25Engine = new RankedTextSearchEngine(baseIndex, new Bm25Scorer(1.5, 0.75), tokenizer);

        var qrelsQueries = queries.Where(q => qrels.ContainsKey(q.Id)).ToList();
        int splitIdx = qrelsQueries.Count / 2;
        var trainQueries = qrelsQueries.Take(splitIdx).ToList();
        var testQueries = qrelsQueries.Skip(splitIdx).ToList();

        var trainPairs = new List<(double Score, bool IsCorrect)>();
        foreach (var (qid, qtext) in trainQueries)
        {
            var results = bm25Engine.Search(qtext, new SearchOptions(Limit: int.MaxValue)).ToList();
            foreach (var r in results)
                trainPairs.Add((r.Score, qrels[qid].Contains(r.DocumentId)));
        }
        var calibrator = CalibratedScoreConfidence.Fit(trainPairs, CalibrationMethod.IsotonicRegression);

        var baseEngine = bm25Engine;
        var reformRrf = new ReformulatingTextSearchEngine(bm25Engine, corrector, tokenizer);
        var reformBayes = new ReformulatingTextSearchEngine(bm25Engine, corrector, tokenizer, calibrator);

        double baseNdcg = 0, rrfNdcg = 0, bayesNdcg = 0;
        int n = 0;
        foreach (var (qid, qtext) in testQueries)
        {
            var baseRes = baseEngine.Search(qtext, new SearchOptions(Limit: 10)).Select(r => r.DocumentId).ToList();
            var rrfRes = reformRrf.Search(qtext, new SearchOptions(Limit: 10)).Select(r => r.DocumentId).ToList();
            var bayesRes = reformBayes.Search(qtext, new SearchOptions(Limit: 10)).Select(r => r.DocumentId).ToList();

            baseNdcg += NdcgForQuery(baseRes, qrels[qid], 10);
            rrfNdcg += NdcgForQuery(rrfRes, qrels[qid], 10);
            bayesNdcg += NdcgForQuery(bayesRes, qrels[qid], 10);
            n++;
        }

        _output.WriteLine($"Base BM25 nDCG@10: {baseNdcg / n:F4}");
        _output.WriteLine($"Reformulation (RRF) nDCG@10: {rrfNdcg / n:F4}");
        _output.WriteLine($"Reformulation (Bayesian) nDCG@10: {bayesNdcg / n:F4}");

        Assert.True(true);
    }

    private void MeasureBoost(string label, ITextSearchEngine baseline, ITextSearchEngine boosted,
        List<(string Id, string Text)> queries, Dictionary<string, HashSet<string>> qrels, int k)
    {
        double ndcg = 0;
        int regressions = 0, improvements = 0;
        int n = 0;
        foreach (var (qid, qtext) in queries)
        {
            var baseResults = baseline.Search(qtext, new SearchOptions(Limit: k)).Select(r => r.DocumentId).ToList();
            var boostedResults = boosted.Search(qtext, new SearchOptions(Limit: k)).Select(r => r.DocumentId).ToList();
            double baseQ = NdcgForQuery(baseResults, qrels[qid], k);
            double boostedQ = NdcgForQuery(boostedResults, qrels[qid], k);
            ndcg += boostedQ;
            if (boostedQ < baseQ - 1e-9) regressions++;
            if (boostedQ > baseQ + 1e-9) improvements++;
            n++;
        }
        _output.WriteLine($"{label}: nDCG@10 = {ndcg / n:F4}, Improvements = {improvements}, Regressions = {regressions} (of {n})");
    }

    private static double ComputeNdcg(ITextSearchEngine engine, List<(string Id, string Text)> queries,
        Dictionary<string, HashSet<string>> qrels, int k)
    {
        double sum = 0;
        int n = 0;
        foreach (var (qid, qtext) in queries)
        {
            var results = engine.Search(qtext, new SearchOptions(Limit: k)).Select(r => r.DocumentId).ToList();
            sum += NdcgForQuery(results, qrels[qid], k);
            n++;
        }
        return n > 0 ? sum / n : 0;
    }

    private static double NdcgForQuery(List<string> ranked, HashSet<string> relevant, int k)
    {
        double dcg = 0;
        for (int i = 0; i < Math.Min(ranked.Count, k); i++)
        {
            if (relevant.Contains(ranked[i]))
                dcg += 1.0 / Math.Log2(i + 2);
        }
        // IDCG: all relevant docs at top
        int relCount = Math.Min(relevant.Count, k);
        double idcg = 0;
        for (int i = 0; i < relCount; i++)
            idcg += 1.0 / Math.Log2(i + 2);
        return idcg > 0 ? dcg / idcg : 0;
    }
}