using LexiSharp;
using LexiSharp.Classification;
using LexiSharp.Core;
using LexiSharp.Embeddings;
using LexiSharp.Linguistics;

namespace LexiSharp.Demo;

/// <summary>
/// Trains the library's two interpretable classifiers on the demo corpus's categories and
/// exposes their predictions together with the evidence behind them, so the demo can show
/// explainability as well as retrieval.
/// </summary>
public sealed class DemoClassificationService
{
    private readonly ITokenizer _tokenizer;
    private readonly IEmbeddingProvider _embeddings;
    private readonly NaiveBayesClassifier _naiveBayes;
    private readonly NaiveBayesClassifier _classSpecificNb;
    private readonly DecisionTreeClassifier _tree;
    private readonly CalibratedClassifier _calibratedTree;
    private readonly LinearClassifier _linear;
    private readonly RandomForestClassifier _forest;
    private readonly CentroidClassifier _centroid;
    private readonly NearestNeighborClassifier _nearest;
    private readonly BlendTextClassifier _blend;
    private readonly SynchronizedTextClassifier _syncLinear;
    private readonly CorpusDivider _divider;
    private readonly IReadOnlyList<SearchDocument> _documents;

    public DemoClassificationService(DemoCorpusSet corpus, WordSegmentation segmentation)
    {
        ArgumentNullException.ThrowIfNull(corpus);

        _tokenizer = new Tokenizer(new TokenizerOptions
        {
            RemoveStopWords = true,
            WordSegmentation = segmentation,
        });

        _embeddings = new HashingEmbeddingProvider(256, _tokenizer);

        IReadOnlyList<SearchDocument> documents = corpus.Documents;

        _naiveBayes = new NaiveBayesClassifier(_tokenizer);
        _naiveBayes.Train(documents);

        _classSpecificNb = new NaiveBayesClassifier(
            _tokenizer,
            new NaiveBayesOptions { ClassSpecific = true, ClassSpecificThreshold = 1.0 });
        _classSpecificNb.Train(documents);

        _tree = new DecisionTreeClassifier(_tokenizer);
        _tree.Train(documents);

        _calibratedTree = CalibratedClassifier.Fit(_tree, documents);

        _linear = new LinearClassifier(_tokenizer);
        _linear.Train(documents);

        _forest = new RandomForestClassifier(_tokenizer);
        _forest.Train(documents);

        _centroid = new CentroidClassifier(_embeddings);
        _centroid.Train(documents);

        _nearest = new NearestNeighborClassifier(_tokenizer);
        _nearest.Train(documents);

        _blend = new BlendTextClassifier(new[]
        {
            new TextClassifierLane(_naiveBayes, 1.0),
            new TextClassifierLane(_tree, 1.0),
            new TextClassifierLane(_linear, 1.0),
        });
        _blend.Train(documents);

        _syncLinear = new SynchronizedTextClassifier(_linear);

        _divider = new CorpusDivider(_tokenizer);
        _documents = corpus.Documents;
    }

    public ClassificationResponse Classify(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var nb = _naiveBayes.Predict(text, limit: 5)
            .Select(r => new ClassificationPredictionDto(r.Category, r.Probability))
            .ToList();

        var tree = _tree.Predict(text, limit: 5)
            .Select(r => new ClassificationPredictionDto(r.Category, r.Probability))
            .ToList();

        var calibratedTree = _calibratedTree.Predict(text, limit: 5)
            .Select(r => new ClassificationPredictionDto(r.Category, r.Probability))
            .ToList();

        var linear = _linear.Predict(text, limit: 5)
            .Select(r => new ClassificationPredictionDto(r.Category, r.Probability))
            .ToList();

        var forest = _forest.Predict(text, limit: 5)
            .Select(r => new ClassificationPredictionDto(r.Category, r.Probability))
            .ToList();

        var centroid = _centroid.Predict(text, limit: 5)
            .Select(r => new ClassificationPredictionDto(r.Category, r.Probability))
            .ToList();

        var nearest = _nearest.Predict(text, limit: 5)
            .Select(r => new ClassificationPredictionDto(r.Category, r.Probability))
            .ToList();

        var blend = _blend.Predict(text, limit: 5)
            .Select(r => new ClassificationPredictionDto(r.Category, r.Probability))
            .ToList();

        var sync = _syncLinear.Predict(text, limit: 5)
            .Select(r => new ClassificationPredictionDto(r.Category, r.Probability))
            .ToList();

        var linearTopFeatures = _linear.TopFeatures(
            linear.Count > 0 ? linear[0].Category : _linear.PredictBest(text) ?? string.Empty,
            8)
            .ToDictionary(f => f.Feature, f => f.Weight, StringComparer.Ordinal);

        var explanation = _tree.Explain(text);
        var path = explanation is null
            ? new List<QuestionAnswerDto>()
            : explanation.Path.Select(qa => new QuestionAnswerDto(qa.Question.Describe(), qa.Answer)).ToList();

        var leaf = explanation?.Outcome;

        var selectedTerms = _classSpecificNb.SelectedTermsByClass.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.OrderBy(t => t, StringComparer.Ordinal).ToList(),
            StringComparer.Ordinal);

        return new ClassificationResponse(
            text,
            nb,
            tree,
            calibratedTree,
            linear,
            forest,
            centroid,
            nearest,
            blend,
            sync,
            path,
            leaf?.Category,
            leaf is null
                ? new Dictionary<string, double>(StringComparer.Ordinal)
                : new Dictionary<string, double>(leaf.Probabilities, StringComparer.Ordinal),
            leaf?.DocumentCount ?? 0,
            selectedTerms,
            linearTopFeatures);
    }

    public void Reinforce(string text, string category)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(category))
            return;

        _naiveBayes.Reinforce(text, category);
        _classSpecificNb.Reinforce(text, category);
        _tree.Reinforce(text, category);
    }

    public void ForgetReinforcement()
    {
        _naiveBayes.ForgetReinforcement();
        _classSpecificNb.ForgetReinforcement();
        _tree.ForgetReinforcement();
    }

    public IReadOnlyList<ClassificationPredictionDto> ClassifyWithContext(
        string text,
        string priorCategory,
        double priorWeight,
        string? candidateCategory = null,
        double candidateWeight = 1.0)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<ClassificationPredictionDto>();

        var context = new BlendContext
        {
            Priors = string.IsNullOrWhiteSpace(priorCategory)
                ? null
                : new[] { new TextEvidence(priorCategory, priorWeight) },
            Candidates = string.IsNullOrWhiteSpace(candidateCategory)
                ? null
                : new[] { new TextEvidence(candidateCategory, candidateWeight) },
        };

        return _blend.Predict(text, context, limit: 5)
            .Select(r => new ClassificationPredictionDto(r.Category, r.Probability))
            .ToList();
    }

    public ClassifierInfoResponse Info() => new(
        _classSpecificNb.SelectedTermsByClass.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.OrderBy(t => t, StringComparer.Ordinal).ToList(),
            StringComparer.Ordinal),
        _tree.Describe());

    public IReadOnlyList<ClusterDto> Divide()
    {
        var division = _divider.Divide(_documents);
        return division.Clusters.Select((c, i) => new ClusterDto(
            $"cluster-{i + 1}",
            c.DocumentIds,
            c.TopFeatures,
            c.Depth)).ToList();
    }

    public string DivideTree() => _divider.Divide(_documents).Describe();

    public (int Bytes, bool RoundTrip) TestPersistence()
    {
        var tree = new DecisionTreeClassifier(_tokenizer);
        tree.Train(_documents);

        using var ms = new MemoryStream();
        ClassificationModelSerializer.Save(tree, ms);
        ms.Position = 0;
        var loaded = ClassificationModelSerializer.LoadDecisionTree(ms, new TextFeatureSource(_tokenizer));

        bool ok = tree.Predict("refresh token", 1).FirstOrDefault()?.Category
            == loaded.Predict("refresh token", 1).FirstOrDefault()?.Category;

        return ((int)ms.Length, ok);
    }

    public OnlineEvalDto OnlineEvaluate()
    {
        var warmup = _documents.Take(5).ToList();
        var stream = _documents.Select((d, i) => new OnlineEvaluationExample(d, Block: i / 5)).ToList();

        var report = OnlineClassificationEvaluator.Evaluate(
            () => new NaiveBayesClassifier(_tokenizer),
            warmup,
            stream,
            new OnlineEvaluationOptions { BootstrapResamples = 50, TopK = 3, Learn = true });

        return new OnlineEvalDto(report.AccuracyAt1, report.MacroF1, report.AccuracyAtK, report.Documents);
    }

    public EvaluationResponse Evaluate(IReadOnlyList<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        if (documents.Count < 4)
            return new EvaluationResponse(Array.Empty<ModelMetricDto>(), 1.0, false, 0, 0);

        // Stratified-ish split: take roughly 30 % of each category for the held-out set, so every
        // category is represented on both sides of a small corpus.
        var train = new List<SearchDocument>();
        var test = new List<SearchDocument>();

        foreach (var group in documents.GroupBy(d => d.Category ?? string.Empty))
        {
            var ordered = group.ToList();
            int testCount = Math.Max(1, (int)Math.Round(ordered.Count * 0.3));

            for (int i = 0; i < ordered.Count; i++)
            {
                if (i < testCount)
                    test.Add(ordered[i]);
                else
                    train.Add(ordered[i]);
            }
        }

        var nb = new NaiveBayesClassifier(_tokenizer);
        var tree = new DecisionTreeClassifier(_tokenizer);
        var linear = new LinearClassifier(_tokenizer);
        var forest = new RandomForestClassifier(_tokenizer);
        var centroid = new CentroidClassifier(_embeddings);

        nb.Train(train);
        tree.Train(train);
        linear.Train(train);
        forest.Train(train);
        centroid.Train(train);

        var calibratedTree = CalibratedClassifier.Fit(tree, test);

        var metrics = new List<ModelMetricDto>
        {
            new("Naive Bayes", Accuracy(nb, test), MacroF1(nb, test)),
            new("Decision tree", Accuracy(tree, test), MacroF1(tree, test)),
            new("Calibrated tree", Accuracy(calibratedTree, test), MacroF1(calibratedTree, test)),
            new("Linear", Accuracy(linear, test), MacroF1(linear, test)),
            new("Random forest", Accuracy(forest, test), MacroF1(forest, test)),
            new("Centroid", Accuracy(centroid, test), MacroF1(centroid, test)),
        };

        // Calibration on the held-out split (used as validation for the demo).
        var samples = test.Select(d => new CalibrationSample(
            nb.Predict(d.Text, limit: int.MaxValue),
            d.Category!)).ToList();
        double temperature = 1.0;
        try
        {
            temperature = TemperatureCalibrator.Fit(samples).Temperature;
        }
        catch
        {
            temperature = 1.0;
        }

        // Persistence round-trip on the decision tree.
        bool persisted = false;
        try
        {
            using var ms = new MemoryStream();
            ClassificationModelSerializer.Save(tree, ms);
            ms.Position = 0;
            var loaded = ClassificationModelSerializer.LoadDecisionTree(ms, new TextFeatureSource(_tokenizer));
            persisted = tree.Predict(test[0].Text, limit: 1).FirstOrDefault()?.Category
                == loaded.Predict(test[0].Text, limit: 1).FirstOrDefault()?.Category;
        }
        catch
        {
            persisted = false;
        }

        return new EvaluationResponse(metrics, temperature, persisted, train.Count, test.Count);
    }

    private static double Accuracy(ITextClassifier model, IReadOnlyList<SearchDocument> test)
    {
        if (test.Count == 0)
            return 0.0;

        int correct = 0;
        foreach (var doc in test)
        {
            if (string.Equals(model.PredictBest(doc.Text), doc.Category, StringComparison.Ordinal))
                correct++;
        }

        return (double)correct / test.Count;
    }

    private static double MacroF1(ITextClassifier model, IReadOnlyList<SearchDocument> test)
    {
        if (test.Count == 0)
            return 0.0;

        var classes = test.Select(d => d.Category!).Distinct(StringComparer.Ordinal).ToList();
        double total = 0.0;

        foreach (string cls in classes)
        {
            int tp = 0, fp = 0, fn = 0;

            foreach (var doc in test)
            {
                string predicted = model.PredictBest(doc.Text) ?? string.Empty;
                bool isActual = string.Equals(doc.Category, cls, StringComparison.Ordinal);
                bool isPredicted = string.Equals(predicted, cls, StringComparison.Ordinal);

                if (isActual && isPredicted) tp++;
                else if (!isActual && isPredicted) fp++;
                else if (isActual && !isPredicted) fn++;
            }

            double precision = tp + fp > 0 ? (double)tp / (tp + fp) : 0.0;
            double recall = tp + fn > 0 ? (double)tp / (tp + fn) : 0.0;
            double f1 = precision + recall > 0 ? (2 * precision * recall) / (precision + recall) : 0.0;
            total += f1;
        }

        return total / classes.Count;
    }
}

public sealed record ClusterDto(string Id, IReadOnlyList<string> DocumentIds, IReadOnlyList<string> TopFeatures, int Depth);

public sealed record OnlineEvalDto(double AccuracyAt1, double MacroF1, double AccuracyAtK, int Documents);

public sealed record ModelMetricDto(string Model, double Accuracy, double MacroF1);

public sealed record EvaluationResponse(
    IReadOnlyList<ModelMetricDto> Metrics,
    double CalibrationTemperature,
    bool PersistenceRoundTrip,
    int TrainCount,
    int TestCount);

public sealed record ClassificationPredictionDto(string Category, double Probability);

public sealed record QuestionAnswerDto(string Question, bool Answer);

public sealed record ClassificationResponse(
    string Text,
    IReadOnlyList<ClassificationPredictionDto> NaiveBayes,
    IReadOnlyList<ClassificationPredictionDto> DecisionTree,
    IReadOnlyList<ClassificationPredictionDto> CalibratedTree,
    IReadOnlyList<ClassificationPredictionDto> Linear,
    IReadOnlyList<ClassificationPredictionDto> RandomForest,
    IReadOnlyList<ClassificationPredictionDto> Centroid,
    IReadOnlyList<ClassificationPredictionDto> NearestNeighbor,
    IReadOnlyList<ClassificationPredictionDto> Blend,
    IReadOnlyList<ClassificationPredictionDto> Synchronized,
    IReadOnlyList<QuestionAnswerDto> TreePath,
    string? TreeLeaf,
    IReadOnlyDictionary<string, double> TreeLeafProbabilities,
    int TreeLeafDocuments,
    IReadOnlyDictionary<string, IReadOnlyList<string>> SelectedTermsByClass,
    IReadOnlyDictionary<string, double> LinearTopFeatures);

public sealed record ClassifierInfoResponse(
    IReadOnlyDictionary<string, IReadOnlyList<string>> SelectedTermsByClass,
    string DecisionTree);
