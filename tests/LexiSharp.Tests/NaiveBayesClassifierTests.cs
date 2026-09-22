using LexiSharp.Classification;
using LexiSharp.Core;
using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

public class NaiveBayesClassifierTests
{
    private static readonly string[] LowestToHighestCategory = { "a", "b", "c" };
    private static readonly string[] OrdinalSortedCategories = { "A", "B", "C" };

    private static readonly SearchDocument[] Training =
    {
        new("1", "internet connection problem", Category: "Support"),
        new("2", "cannot access the website", Category: "Support"),
        new("3", "forgotten password", Category: "Support"),
        new("4", "monthly consumption invoice", Category: "Billing"),
        new("5", "online invoice statement", Category: "Billing"),
        new("6", "card payment problem", Category: "Billing"),
        new("7", "delivery delay too long", Category: "Shipping"),
        new("8", "tracking my order", Category: "Shipping"),
    };

    private static NaiveBayesClassifier Trained()
    {
        var classifier = new NaiveBayesClassifier();
        classifier.Train(Training);
        return classifier;
    }

    [Fact]
    public void Predict_AssignsDocumentToItsMatchingCategory()
    {
        var classifier = Trained();

        var support = classifier.Predict("i cannot connect to the internet", limit: 3);
        Assert.Equal("Support", support[0].Category);

        var billing = classifier.Predict("i would like my december invoice");
        Assert.Equal("Billing", billing[0].Category);

        var delivery = classifier.Predict("i am waiting for my parcel delivery");
        Assert.Equal("Shipping", delivery[0].Category);
    }

    [Fact]
    public void Predict_ReturnsProbabilitiesThatSumToOne()
    {
        var classifier = Trained();

        var results = classifier.Predict("password", limit: 3);

        Assert.Equal(3, results.Count);
        Assert.Equal(1.0, results.Sum(r => r.Probability), precision: 10);
        Assert.Equal("Support", results[0].Category);
    }

    [Fact]
    public void Predict_OfUnknownText_FallsBackToClassPriors()
    {
        const string pattern = "aaa bbb ccc"; // 3 tokens per class, each class unique vocabulary
        var classifier = new NaiveBayesClassifier();
        classifier.Train(new[]
        {
            new SearchDocument("1", pattern, Category: "A"),
            new SearchDocument("2", "ddd eee fff", Category: "B"),
            new SearchDocument("3", "ggg hhh iii", Category: "C"),
        });

        var results = classifier.Predict("zzz www", limit: 3);

        // Equal class sizes and equal document counts => uniform prior, no term evidence.
        Assert.All(results, r => Assert.Equal(1.0 / 3, r.Probability, precision: 10));
    }

    [Fact]
    public void PredictBest_ReturnsSingleCategory()
    {
        var classifier = Trained();

        Assert.Equal("Support", classifier.PredictBest("internet connection"));
        Assert.Equal("Shipping", classifier.PredictBest("order delivery"));
    }

    [Fact]
    public void Predict_ReturnsEmpty_BeforeTraining()
    {
        var classifier = new NaiveBayesClassifier();

        Assert.Empty(classifier.Predict("anything"));
        Assert.Null(classifier.PredictBest("anything"));
    }

    [Fact]
    public void Train_IgnoresDocumentsWithoutCategory()
    {
        var classifier = new NaiveBayesClassifier();
        classifier.Train(new[]
        {
            new SearchDocument("1", "uncategorised text"),
            new SearchDocument("2", "categorised text", Category: "A"),
        });

        var results = classifier.Predict("categorised");

        Assert.Single(results);
        Assert.Equal("A", results[0].Category);
    }

    [Fact]
    public void Train_ReplacesPreviousModel()
    {
        var classifier = new NaiveBayesClassifier();
        classifier.Train(new[] { new SearchDocument("1", "alpha", Category: "X") });
        classifier.Train(new[] { new SearchDocument("2", "beta", Category: "Y") });

        Assert.Equal("Y", classifier.PredictBest("beta"));
        Assert.DoesNotContain(classifier.Predict("alpha"), r => r.Category == "X");
    }

    [Fact]
    public void Predict_RespectsLimit()
    {
        var classifier = new NaiveBayesClassifier();
        classifier.Train(Training);

        Assert.Empty(classifier.Predict("invoice", 0));
        Assert.Single(classifier.Predict("invoice", 1));
    }

    [Fact]
    public void Predict_EqualProbabilities_OrderByCategory()
    {
        const string pattern = "aaa bbb ccc";
        var classifier = new NaiveBayesClassifier();
        classifier.Train(new[]
        {
            new SearchDocument("1", pattern, Category: "b"),
            new SearchDocument("2", "ddd eee fff", Category: "a"),
            new SearchDocument("3", "ggg hhh iii", Category: "c"),
        });

        var results = classifier.Predict("zzz www", limit: 3);

        // Nothing matches and priors are uniform: deterministic order by category name.
        Assert.Equal(LowestToHighestCategory, results.Select(r => r.Category).ToArray());
        Assert.All(results, r => Assert.Equal(1.0 / 3, r.Probability, precision: 10));
    }

    [Fact]
    public void Predict_TrailingEmptyClasses_StayFinite()
    {
        var classifier = new NaiveBayesClassifier();
        classifier.Train(new[]
        {
            new SearchDocument("1", "", Category: "A"),
            new SearchDocument("2", "", Category: "B"),
            new SearchDocument("3", "real tokens", Category: "C"),
        });

        var results = classifier.Predict("anything", limit: 3);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.False(double.IsNaN(r.Probability)));
        Assert.Equal(1.0, results.Sum(r => r.Probability), precision: 10);
    }

    [Fact]
    public void Predict_ExcludedCategories_RenormalizesOverTheRest()
    {
        var classifier = Trained();

        var full = classifier.Predict("forgotten password", limit: 10);
        Assert.Equal(3, full.Count);
        Assert.Equal("Support", full[0].Category);

        var excluded = classifier.Predict("forgotten password", limit: 10, excludedCategories: new HashSet<string> { "Support" });

        Assert.Equal(2, excluded.Count);
        Assert.DoesNotContain(excluded, r => r.Category == "Support");
        Assert.Equal(1.0, excluded.Sum(r => r.Probability), precision: 10);
    }

    [Fact]
    public void PredictBest_ExcludedCategory_FallsBackToSecondBest()
    {
        var classifier = Trained();

        Assert.Equal("Support", classifier.PredictBest("forgotten password"));
        Assert.Equal("Billing", classifier.PredictBest(
            "forgotten password", excludedCategories: new HashSet<string> { "Support" }));
    }

    [Fact]
    public void Predict_AllCategoriesExcluded_ReturnsEmpty()
    {
        var classifier = Trained();

        Assert.Empty(classifier.Predict(
            "anything", limit: 3, excludedCategories: new HashSet<string> { "Support", "Billing", "Shipping" }));
        Assert.Null(classifier.PredictBest(
            "anything", excludedCategories: new HashSet<string> { "Support", "Billing", "Shipping" }));
    }

    [Fact]
    public void Ctor_RejectsInvalidTemperature()
    {
        Assert.Throws<ArgumentException>(() => new NaiveBayesClassifier(options: new NaiveBayesOptions { Temperature = 0 }));
        Assert.Throws<ArgumentException>(() => new NaiveBayesClassifier(options: new NaiveBayesOptions { Temperature = double.NaN }));
    }

    [Fact]
    public void Temperature_SharpensOrFlattensThePosterior()
    {
        var cold = new NaiveBayesClassifier(options: new NaiveBayesOptions { Temperature = 0.5 });
        cold.Train(Training);

        var hot = new NaiveBayesClassifier(options: new NaiveBayesOptions { Temperature = 4.0 });
        hot.Train(Training);

        var baseline = Trained().Predict("forgotten password")[0].Probability;
        var sharpened = cold.Predict("forgotten password")[0].Probability;
        var flattened = hot.Predict("forgotten password")[0].Probability;

        Assert.True(sharpened > baseline, "T < 1 should sharpen the posterior.");
        Assert.True(flattened < baseline, "T > 1 should flatten the posterior.");
        Assert.Equal("Support", cold.Predict("forgotten password")[0].Category);
        Assert.Equal("Support", hot.Predict("forgotten password")[0].Category);
    }

    [Fact]
    public void IdfDocumentCount_LetRareTermOutweighACorpusWideTerm()
    {
        SearchDocument[] corpus =
        {
            new("1", "rare common", Category: "A"),
        };

        for (int i = 0; i < 50; i++)
            corpus = corpus.Append(new SearchDocument($"b{i}", "common", Category: "B")).ToArray();

        var baseline = new NaiveBayesClassifier();
        baseline.Train(corpus);
        Assert.Equal("B", baseline.PredictBest("rare common"));

        var weighted = new NaiveBayesClassifier(options: new NaiveBayesOptions { IdfMode = IdfMode.DocumentCount });
        weighted.Train(corpus);
        Assert.Equal("A", weighted.PredictBest("rare common"));
    }

    [Fact]
    public void IdfClassCount_DropsTermsPresentInEveryClass()
    {
        // "port" appears in every class (C = 2, df = 2 => idf = log(2/2) = 0 => no evidence),
        // "wifi" is class-discriminative and decides entirely on its own.
        SearchDocument[] corpus =
        {
            new("1", "port wifi", Category: "A"),
            new("2", "port", Category: "B"),
        };

        var classifier = new NaiveBayesClassifier(options: new NaiveBayesOptions { IdfMode = IdfMode.ClassCount });
        classifier.Train(corpus);

        Assert.Equal("A", classifier.PredictBest("wifi"));

        // "port" contributes nothing to either class: priors are tied, so the posterior is uniform.
        var port = classifier.Predict("port", limit: 2);
        Assert.Equal(2, port.Count);
        Assert.All(port, r => Assert.Equal(0.5, r.Probability, precision: 10));
    }

    [Fact]
    public void SkipOutOfVocabularyTokens_UnknownTermsAddNoEvidence()
    {
        SearchDocument[] corpus =
        {
            new("1", "chat", Category: "A"),
            new("2", "chat", Category: "A"),
            new("3", "mail", Category: "B"),
        };

        var classifier = new NaiveBayesClassifier(options: new NaiveBayesOptions { SkipOutOfVocabularyTokens = true });
        classifier.Train(corpus);

        // "zzz" is unknown: skipped, so the class prior alone decides (A has 2 of 3 documents) —
        // instead of the Laplace penalty dragging each class down by its vocabulary size.
        Assert.Equal("A", classifier.PredictBest("zzz"));
        Assert.Equal("A", classifier.PredictBest("zzz chat"));
    }

    [Fact]
    public void SmoothPriors_CompressesThePriorSpread()
    {
        SearchDocument[] corpus =
        {
            new("1", "alpha", Category: "A"),
            new("2", "delta", Category: "A"),
            new("3", "epsilon", Category: "A"),
            new("4", "zeta", Category: "A"),
            new("5", "eta", Category: "A"),
            new("6", "theta", Category: "A"),
            new("7", "iota", Category: "A"),
            new("8", "kappa", Category: "A"),
            new("9", "beta", Category: "B"),
            new("10", "gamma", Category: "C"),
        };

        var plain = new NaiveBayesClassifier(options: new NaiveBayesOptions { SkipOutOfVocabularyTokens = true });
        plain.Train(corpus);

        var smooth = new NaiveBayesClassifier(options: new NaiveBayesOptions { SkipOutOfVocabularyTokens = true, SmoothPriors = true });
        smooth.Train(corpus);

        // Pure-prior prediction (nothing in vocabulary): smoothing pulls the dominant class
        // back toward uniform (8/10 = 0.8 => (8+1)/(10+3) = 9/13).
        var plainTop = plain.Predict("zzzz")[0].Probability;
        var smoothTop = smooth.Predict("zzzz")[0].Probability;

        Assert.True(smoothTop > 0);
        Assert.True(smoothTop < plainTop, "Smoothing should compress the prior spread.");
    }

    [Fact]
    public void Alpha_FlattensTheSmoothedLikelihood()
    {
        SearchDocument[] corpus =
        {
            new("1", "chat", Category: "A"),
            new("2", "chat", Category: "A"),
            new("3", "mail", Category: "B"),
        };

        var sharp = new NaiveBayesClassifier(options: new NaiveBayesOptions { Alpha = 0.1 });
        sharp.Train(corpus);

        var flat = new NaiveBayesClassifier(options: new NaiveBayesOptions { Alpha = 50.0 });
        flat.Train(corpus);

        var sharpA = sharp.Predict("chat")[0].Probability;
        var flatA = flat.Predict("chat")[0].Probability;

        Assert.True(sharpA > flatA, "Small alpha sharpens the evidence of a repeated term.");
    }

    [Fact]
    public void WeightedTokens_ReduceTheEvidenceOfACorrectedToken()
    {
        SearchDocument[] corpus =
        {
            new("1", "chat", Category: "A"),
            new("2", "chat", Category: "A"),
            new("3", "support", Category: "B"),
            new("4", "support", Category: "B"),
        };

        var classifier = new NaiveBayesClassifier();
        classifier.Train(corpus);
        NaiveBayesClassifier predictor = classifier;

        Assert.Equal("A", predictor.Predict(new[] { new WeightedToken("chat", 1.0), new WeightedToken("support", 0.9) }, 1)[0].Category);
        Assert.Equal("A", predictor.Predict(new[] { new WeightedToken("chat", 1.0), new WeightedToken("support", 0.4) }, 1)[0].Category);
        Assert.Equal("B", predictor.Predict(new[] { new WeightedToken("chat", 0.4), new WeightedToken("support", 1.0) }, 1)[0].Category);
    }

    [Fact]
    public void Predict_TokenWeightOfZero_ContributesNoEvidence()
    {
        SearchDocument[] corpus =
        {
            new("1", "chat", Category: "A"),
            new("2", "support", Category: "B"),
        };

        var classifier = new NaiveBayesClassifier();
        classifier.Train(corpus);
        NaiveBayesClassifier predictor = classifier;

        // Zeroed-weighted token behaves as if absent: B wins on "support" alone.
        var results = predictor.Predict(new[] { new WeightedToken("chat", 0.0), WeightedToken.Full("support") }, 1);
        Assert.Equal("B", results[0].Category);
    }

    [Fact]
    public void Parity_WithWeightedIdfSmoothedReferenceScoring()
    {
        // Reference semantics (out-of-vocabulary tokens skipped, class-count IDF, alpha-smoothed
        // priors and likelihood, fractional token weights, softmax temperature): exercised by a
        // grammar-heavy corpus with an uneven prior, a corpus-wide term and an out-of-vocabulary
        // token so every knob has to do something.
        SearchDocument[] corpus =
        {
            new("1", "one two", Category: "A"),
            new("2", "one", Category: "A"),
            new("3", "two", Category: "A"),
            new("4", "three four", Category: "B"),
            new("5", "three", Category: "B"),
            new("6", "four", Category: "B"),
            new("7", "five", Category: "C"),
        };

        const double alpha = 1.0;
        const double temperature = 1.0;

        var classifier = new NaiveBayesClassifier(
            tokenizer: null,
            options: new NaiveBayesOptions
            {
                IdfMode = IdfMode.ClassCount,
                Alpha = alpha,
                SmoothPriors = true,
                SkipOutOfVocabularyTokens = true,
                Temperature = temperature,
            });
        classifier.Train(corpus);

        var tokenizer = Tokenizer.Default;
        var model = ReferenceModel.Build(corpus, tokenizer);
        var query = new[]
        {
            new WeightedToken("one", 1.0),
            new WeightedToken("three", 0.8),
            new WeightedToken("six", 0.5),
        };

        var actual = classifier.Predict(query, limit: 3);
        var expected = ReferenceScore(model, query, alpha, temperature);

        Assert.Equal(expected.Count, actual.Count);
        foreach (var result in actual)
        {
            Assert.True(expected.TryGetValue(result.Category, out double referenceProbability),
                $"Category '{result.Category}' missing from reference.");
            Assert.Equal(referenceProbability, result.Probability, precision: 8);
        }
    }

    // Reference: scikit-learn 1.9.1 ComplementNB(alpha=1.0, fit_prior=True, norm=False) on the
    // exact corpus below. Gold probabilities come from that implementation (features spelled out
    // as count vectors; no tokenizer ambiguity), not from this code.
    [Fact]
    public void Complement_Parity_WithScikitLearnReferenceScoring()
    {
        SearchDocument[] corpus =
        {
            new("1", "cat chat", Category: "A"),
            new("2", "cat chat cat", Category: "A"),
            new("3", "chat cat", Category: "A"),
            new("4", "cat chat chat", Category: "A"),
            new("5", "cat", Category: "A"),
            new("6", "cat chat gopher", Category: "A"),
            new("7", "dog dog cat", Category: "B"),
            new("8", "dog cat dog", Category: "B"),
            new("9", "dog duck", Category: "B"),
            new("10", "mole gopher", Category: "C"),
            new("11", "duck mole", Category: "C"),
            new("12", "gopher gopher mole", Category: "C"),
        };

        var classifier = new NaiveBayesClassifier(
            tokenizer: null,
            options: new NaiveBayesOptions { Complement = true });
        classifier.Train(corpus);

        (string Query, string Best, double[] Proba)[] queries =
        {
            ("dog", "B", new[] { 0.09952606635071089, 0.7677725118483412, 0.13270142180094788 }),
            ("cat dog", "B", new[] { 0.190377517321764, 0.7080878067732954, 0.10153467590494078 }),
            ("gopher mole", "C", new[] { 0.06044242208272798, 0.07993201940736272, 0.8596255585099094 }),
            ("duck", "C", new[] { 0.20289855072463767, 0.39130434782608686, 0.4057971014492754 }),
            ("cat", "A", new[] { 0.5313092979127134, 0.2561669829222011, 0.21252371916508533 }),
            ("cat cat", "A", new[] { 0.7181525891049657, 0.16694299663823975, 0.11490441425679446 }),
            ("gopher", "C", new[] { 0.21298174442190662, 0.21906693711967543, 0.5679513184584178 }),
        };

        foreach (var query in queries)
        {
            var results = classifier.Predict(query.Query, limit: 3).ToDictionary(r => r.Category, r => r.Probability);

            Assert.Equal(query.Best, classifier.PredictBest(query.Query));
            Assert.Equal(OrdinalSortedCategories, classifier.Predict(query.Query, limit: 3).Select(r => r.Category).OrderBy(c => c, StringComparer.Ordinal));

            Assert.Equal(query.Proba[0], results["A"], precision: 6);
            Assert.Equal(query.Proba[1], results["B"], precision: 6);
            Assert.Equal(query.Proba[2], results["C"], precision: 6);
        }
    }

    // Complement Naive Bayes learns a class from its *exclusion*; when the majority class is
    // polluted by the minority's vocabulary, the standard prior can hide the minority — the
    // complement form restores it. Gold (scikit-learn 1.9.1): MultinomialNB picks A (0.558 vs
    // 0.325), ComplementNB picks B (0.584 vs 0.126).
    [Fact]
    public void Complement_RescuesTheRareClass_WhenTheMajorityPollutesTheEvidence()
    {
        SearchDocument[] corpus = Enumerable.Range(0, 10)
            .Select(i => new SearchDocument(i.ToString(), "noise alpha", Category: "A"))
            .Concat(new[]
            {
                new SearchDocument("A10", "noise beta", Category: "B"),
                new SearchDocument("A11", "gamma", Category: "C"),
            })
            .ToArray();

        var standard = new NaiveBayesClassifier();
        standard.Train(corpus);

        var complement = new NaiveBayesClassifier(options: new NaiveBayesOptions { Complement = true });
        complement.Train(corpus);

        Assert.Equal("A", standard.PredictBest("beta noise"));
        Assert.Equal("B", complement.PredictBest("beta noise"));
    }

    private sealed class ReferenceModel
    {
        public Dictionary<string, double> ClassCounts { get; } = new();
        public Dictionary<string, Dictionary<string, double>> TokenCounts { get; } = new();
        public Dictionary<string, double> TotalTokensPerClass { get; } = new();
        public HashSet<string> Vocabulary { get; } = new();
        public Dictionary<string, int> DocumentFrequency { get; } = new();

        public static ReferenceModel Build(
            IEnumerable<SearchDocument> documents, Tokenizer tokenizer)
        {
            var model = new ReferenceModel();

            foreach (var document in documents)
            {
                if (string.IsNullOrEmpty(document.Category))
                    continue;

                string category = document.Category;
                model.ClassCounts.TryAdd(category, 0);
                model.ClassCounts[category] += 1;

                model.TokenCounts.TryAdd(category, new Dictionary<string, double>());
                model.TotalTokensPerClass.TryAdd(category, 0);
                var map = model.TokenCounts[category];

                var seen = new HashSet<string>();
                foreach (var token in tokenizer.Tokenize(document.Text))
                {
                    model.Vocabulary.Add(token);
                    map.TryGetValue(token, out double count);
                    map[token] = count + 1.0;
                    model.TotalTokensPerClass[category] += 1.0;

                    if (seen.Add(token))
                    {
                        model.DocumentFrequency.TryGetValue(token, out int df);
                        model.DocumentFrequency[token] = df + 1;
                    }
                }
            }

            return model;
        }
    }

    // Verbatim port of the reference NaiveBayesEngine.Score (alpha-smoothed, class-count IDF,
    // out-of-vocabulary tokens skipped, cumulative weights, softmax temperature).
    private static Dictionary<string, double> ReferenceScore(
        ReferenceModel model,
        IEnumerable<WeightedToken> weightedTokens,
        double alpha,
        double softmaxTemperature)
    {
        int classCount = model.ClassCounts.Count;
        if (classCount == 0)
            return new Dictionary<string, double>();

        var tokenArray = weightedTokens.ToArray();
        double totalDocs = model.ClassCounts.Values.Sum();
        int vocabSize = Math.Max(1, model.Vocabulary.Count);

        var tokenWeights = new Dictionary<string, double>(tokenArray.Length);
        foreach (var token in tokenArray)
        {
            tokenWeights.TryGetValue(token.Token, out double weight);
            tokenWeights[token.Token] = weight + token.Weight;
        }

        var logScores = new Dictionary<string, double>();
        foreach (string cls in model.ClassCounts.Keys)
        {
            double logPrior = Math.Log((model.ClassCounts[cls] + alpha) / (totalDocs + alpha * classCount));

            double logLikelihood = 0;
            model.TokenCounts.TryGetValue(cls, out var map);
            double totalTokens = model.TotalTokensPerClass.GetValueOrDefault(cls, 0);
            double denom = totalTokens + alpha * vocabSize;

            foreach ((string t, double weight) in tokenWeights)
            {
                if (!model.DocumentFrequency.TryGetValue(t, out int df) || df <= 0)
                    continue;

                double c = 0;
                map?.TryGetValue(t, out c);
                double idf = Math.Max(0.0, Math.Log((double)classCount / df));
                logLikelihood += weight * idf * Math.Log((c + alpha) / denom);
            }

            logScores[cls] = logPrior + logLikelihood;
        }

        if (logScores.Count == 0)
            return new Dictionary<string, double>();

        double max = logScores.Values.Max();
        var exp = logScores.ToDictionary(kv => kv.Key, kv => Math.Exp((kv.Value - max) / softmaxTemperature));
        double sum = exp.Values.Sum();
        return exp.ToDictionary(kv => kv.Key, kv => kv.Value / sum);
    }
}