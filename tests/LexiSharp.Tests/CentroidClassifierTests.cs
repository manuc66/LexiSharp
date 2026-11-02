using System.Collections.Concurrent;
using LexiSharp.Classification;
using LexiSharp.Core;
using LexiSharp.Embeddings;
using Xunit;

namespace LexiSharp.Tests;

public class CentroidClassifierTests
{
    private static readonly float[] Left = { 1f, 0f };
    private static readonly float[] Right = { 0f, 1f };

    private static FakeEmbeddings TwoClasses() =>
        new FakeEmbeddings(dimension: 2)
            .Add("A one", Left)
            .Add("A two", new[] { 0.9f, 0.1f })
            .Add("B one", Right)
            .Add("B two", new[] { 0.1f, 0.9f })
            .Add("A one B one", new[] { 0.5f, 0.5f });

    private static readonly SearchDocument[] TwoClassCorpus =
    {
        new("1", "A one", Category: "A"),
        new("2", "A two", Category: "A"),
        new("3", "B one", Category: "B"),
        new("4", "B two", Category: "B"),
    };

    [Fact]
    public void Predict_AssignsToTheNearestCentroid()
    {
        var classifier = new CentroidClassifier(TwoClasses());
        classifier.Train(TwoClassCorpus);

        Assert.Equal("A", classifier.PredictBest("A one"));
        Assert.Equal("B", classifier.PredictBest("B one"));
    }

    [Fact]
    public void Predict_ReturnsProbabilitiesThatSumToOne()
    {
        var classifier = new CentroidClassifier(TwoClasses());
        classifier.Train(TwoClassCorpus);

        var results = classifier.Predict("A one", limit: 2);

        Assert.Equal(2, results.Count);
        Assert.Equal("A", results[0].Category);
        Assert.Equal(1.0, results.Sum(result => result.Probability), precision: 10);
    }

    [Fact]
    public void Predict_OrdersEqualScoresByCategoryName()
    {
        var classifier = new CentroidClassifier(new FakeEmbeddings(dimension: 2)
            .Add("a", Left)
            .Add("b", Right)
            .Add("tie", new[] { 1f, 1f }));
        classifier.Train(new[]
        {
            new SearchDocument("1", "b", Category: "B"),
            new SearchDocument("2", "a", Category: "A"),
        });

        var results = classifier.Predict("tie", limit: 2);

        Assert.Equal(new[] { "A", "B" }, results.Select(result => result.Category));
        Assert.Equal(0.5, results[0].Probability, precision: 10);
    }

    [Fact]
    public void Predict_ReturnsEmpty_BeforeTraining()
    {
        var embeddings = TwoClasses();
        var classifier = new CentroidClassifier(embeddings);

        Assert.Empty(classifier.Predict("A one"));
        Assert.Null(classifier.PredictBest("A one"));
        Assert.Empty(embeddings.SingleCalls);
    }

    [Fact]
    public void Predict_ReturnsEmpty_ForNonPositiveLimit()
    {
        var classifier = new CentroidClassifier(TwoClasses());
        classifier.Train(TwoClassCorpus);

        Assert.Empty(classifier.Predict("A one", limit: 0));
        Assert.Null(classifier.PredictBest("A one", new HashSet<string>(StringComparer.Ordinal) { "A", "B" }));
    }

    [Fact]
    public void Predict_RespectsExcludedCategories_AndRenormalizes()
    {
        var classifier = new CentroidClassifier(TwoClasses());
        classifier.Train(TwoClassCorpus);

        var results = classifier.Predict(
            "A one",
            limit: 2,
            excludedCategories: new HashSet<string>(StringComparer.Ordinal) { "A" });

        Assert.Single(results);
        Assert.Equal("B", results[0].Category);
        Assert.Equal(1.0, results[0].Probability, precision: 10);
    }

    [Fact]
    public void Predict_RespectsLimit()
    {
        var classifier = new CentroidClassifier(new FakeEmbeddings(dimension: 2)
            .Add("a", Left)
            .Add("b", Right)
            .Add("c", new[] { 0f, -1f }));
        classifier.Train(new[]
        {
            new SearchDocument("1", "a", Category: "A"),
            new SearchDocument("2", "b", Category: "B"),
            new SearchDocument("3", "c", Category: "C"),
        });

        Assert.Single(classifier.Predict("a", limit: 1));
        Assert.Equal(2, classifier.Predict("a", limit: 2).Count);
    }

    [Fact]
    public void Train_ComputesTheMeanOfEachCategory()
    {
        var classifier = new CentroidClassifier(new FakeEmbeddings(dimension: 2)
            .Add("first", new[] { 2f, 0f })
            .Add("second", new[] { 0f, 4f }));
        classifier.Train(new[]
        {
            new SearchDocument("1", "first", Category: "A"),
            new SearchDocument("2", "second", Category: "A"),
        });

        var centroid = classifier.Centroids["A"].Span;

        Assert.Equal(1f, centroid[0], precision: 6);
        Assert.Equal(2f, centroid[1], precision: 6);
        Assert.Equal(2, classifier.Dimension);
        Assert.Equal(1, classifier.CategoryCount);
    }

    [Fact]
    public void Train_IgnoresDocumentsWithoutCategory()
    {
        var embeddings = TwoClasses()
            .Add("unlabelled", new[] { 0.5f, 0.5f });
        var classifier = new CentroidClassifier(embeddings);
        classifier.Train(new[]
        {
            new SearchDocument("1", "unlabelled"),
            new SearchDocument("2", "A one", Category: "A"),
        });

        Assert.Single(classifier.Centroids);
        Assert.Equal("A", classifier.PredictBest("A one"));

        var batch = Assert.Single(embeddings.BatchCalls);
        Assert.Equal(new[] { "A one" }, batch.Texts);
    }

    [Fact]
    public void Train_ReplacesThePreviousModel()
    {
        var classifier = new CentroidClassifier(TwoClasses());
        classifier.Train(TwoClassCorpus);
        classifier.Train(new[] { new SearchDocument("9", "A one", Category: "X") });

        Assert.Equal(new[] { "X" }, classifier.Centroids.Keys);
        Assert.Equal("X", classifier.PredictBest("A one"));
    }

    [Fact]
    public void Train_UsesTheBatchProviderOnce()
    {
        var embeddings = TwoClasses();
        var classifier = new CentroidClassifier(embeddings);

        classifier.Train(TwoClassCorpus);

        var batch = Assert.Single(embeddings.BatchCalls);
        Assert.Equal(EmbeddingUse.Query, batch.Use);
        Assert.Equal(TwoClassCorpus.Length, batch.Texts.Count);
        Assert.Empty(embeddings.SingleCalls);
    }

    [Fact]
    public void TrainingAndPredictionForwardTheirEmbeddingUse()
    {
        var embeddings = TwoClasses();
        var classifier = new CentroidClassifier(embeddings, new CentroidClassifierOptions
        {
            TrainingUse = EmbeddingUse.Passage,
            PredictionUse = EmbeddingUse.Query,
        });

        classifier.Train(TwoClassCorpus);
        classifier.Predict("A one");

        Assert.Equal(EmbeddingUse.Passage, Assert.Single(embeddings.BatchCalls).Use);
        Assert.Equal(EmbeddingUse.Query, Assert.Single(embeddings.SingleCalls).Use);
    }

    [Fact]
    public void Train_FallsBackToOneCallPerDocument()
    {
        var embeddings = TwoClasses();
        IEmbeddingProvider single = new SingleOnlyEmbeddings(embeddings);
        var classifier = new CentroidClassifier(single);

        classifier.Train(TwoClassCorpus);

        Assert.Equal(TwoClassCorpus.Length, embeddings.SingleCalls.Count);
    }

    [Fact]
    public void TemperatureSharpensThePosterior_WithoutChangingTheRanking()
    {
        static CentroidClassifier Trained(double temperature)
        {
            var classifier = new CentroidClassifier(
                new FakeEmbeddings(dimension: 2)
                    .Add("a", Left)
                    .Add("b", Right)
                    .Add("mid", new[] { 0.9f, 0.1f }),
                new CentroidClassifierOptions { Temperature = temperature });

            classifier.Train(new[]
            {
                new SearchDocument("1", "a", Category: "A"),
                new SearchDocument("2", "b", Category: "B"),
            });

            return classifier;
        }

        var flat = Trained(1.0).Predict("mid", limit: 2);
        var sharp = Trained(0.01).Predict("mid", limit: 2);

        Assert.Equal("A", flat[0].Category);
        Assert.Equal("A", sharp[0].Category);
        Assert.True(sharp[0].Probability > flat[0].Probability);
    }

    [Fact]
    public void AQueryOrthogonalToEveryCentroid_ReturnsAUniformPosterior()
    {
        var classifier = new CentroidClassifier(new FakeEmbeddings(dimension: 2)
            .Add("a", Left)
            .Add("b", Right)
            .Add("zero-query", new[] { 0f, 0f }));
        classifier.Train(new[]
        {
            new SearchDocument("1", "a", Category: "B"),
            new SearchDocument("2", "b", Category: "A"),
        });

        var results = classifier.Predict("zero-query", limit: 2);

        Assert.Equal(new[] { "A", "B" }, results.Select(result => result.Category));
        Assert.All(results, result => Assert.Equal(0.5, result.Probability, precision: 10));
    }

    [Fact]
    public void AClassOfZeroVectors_KeepsAZeroCentroid()
    {
        var classifier = new CentroidClassifier(new FakeEmbeddings(dimension: 2)
            .Add("zero", new[] { 0f, 0f })
            .Add("left", Left)
            .Add("left-query", Left));
        classifier.Train(new[]
        {
            new SearchDocument("1", "zero", Category: "Zero"),
            new SearchDocument("2", "left", Category: "Left"),
        });

        Assert.Equal(0f, classifier.Centroids["Zero"].Span[0]);
        Assert.Equal("Left", classifier.PredictBest("left-query"));
    }

    [Fact]
    public void Train_RejectsVectorsOfDifferentLengths()
    {
        var classifier = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category);

        var exception = Assert.Throws<ArgumentException>(() => classifier.Train(new[]
        {
            ("A", Left),
            ("B", new[] { 0f, 1f, 0f }),
        }));

        Assert.Contains("same length", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Train_RejectsNonFiniteVectors()
    {
        var classifier = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category);

        Assert.Throws<ArgumentOutOfRangeException>(() => classifier.Train(new[]
        {
            ("A", new[] { float.NaN, 0f }),
        }));
    }

    [Fact]
    public void TheTextWrapper_RejectsAProviderThatReturnsNonFiniteValues()
    {
        var classifier = new CentroidClassifier(new FakeEmbeddings(dimension: 2)
            .Add("bad", new[] { float.NaN, 0f }));

        Assert.Throws<InvalidOperationException>(() => classifier.Train(new[]
        {
            new SearchDocument("1", "bad", Category: "A"),
        }));
    }

    [Fact]
    public void Predict_RejectsAQueryOfAnotherDimension()
    {
        var classifier = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category);
        classifier.Train(new[] { ("A", Left), ("B", Right) });

        Assert.Throws<ArgumentException>(() => classifier.Predict(new[] { 1f, 0f, 0f }));
    }

    [Fact]
    public void Predict_RejectsANonFiniteQuery()
    {
        var classifier = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category);
        classifier.Train(new[] { ("A", Left), ("B", Right) });

        Assert.Throws<ArgumentOutOfRangeException>(() => classifier.Predict(new[] { float.PositiveInfinity, 0f }));
    }

    [Fact]
    public void Train_RejectsAProviderThatBreaksItsDeclaredDimension()
    {
        var classifier = new CentroidClassifier(new FakeEmbeddings(dimension: 2)
            .Add("a", new[] { 1f, 0f, 0f }));

        var exception = Assert.Throws<InvalidOperationException>(() => classifier.Train(new[]
        {
            new SearchDocument("1", "a", Category: "A"),
        }));

        Assert.Contains("Dimension=2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Train_RejectsAProviderThatBreaksItsBatchContract()
    {
        var classifier = new CentroidClassifier(new ShortBatchEmbeddings());

        var exception = Assert.Throws<InvalidOperationException>(() => classifier.Train(new[]
        {
            new SearchDocument("1", "a", Category: "A"),
            new SearchDocument("2", "b", Category: "B"),
        }));

        Assert.Contains("1 embeddings for 2 documents", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsyncMethods_MatchTheBlockingOnes()
    {
        var sync = new CentroidClassifier(TwoClasses());
        sync.Train(TwoClassCorpus);

        var asynchronous = new CentroidClassifier(TwoClasses());
        await asynchronous.TrainAsync(TwoClassCorpus);

        var expected = sync.Predict("A one", limit: 2);
        var actual = await asynchronous.PredictAsync("A one", limit: 2);

        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Category, actual[i].Category);
            Assert.Equal(expected[i].Probability, actual[i].Probability, precision: 10);
        }
    }

    [Fact]
    public void TheGenericClassifier_ClassifiesItemsThatAreNotText()
    {
        var classifier = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category);

        classifier.Train(new[]
        {
            ("Morning", new[] { 1f, 0f }),
            ("Night", new[] { 0f, 1f }),
        });

        Assert.Equal("Morning", classifier.PredictBest(new[] { 1f, 0.1f }));
        Assert.Equal("Night", classifier.PredictBest(new[] { 0.1f, 1f }));
    }

    [Fact]
    public void TheGenericClassifier_ExposesItsTemperature()
    {
        var classifier = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category,
            new CentroidClassifierOptions { Temperature = 0.25 });

        Assert.Equal(0.25, classifier.Temperature);
    }

    [Fact]
    public void TheTemperatureMustBePositiveAndFinite()
    {
        Assert.Throws<ArgumentException>(() =>
            new CentroidClassifier(new FakeEmbeddings(dimension: 2), new CentroidClassifierOptions { Temperature = 0 }));
        Assert.Throws<ArgumentException>(() =>
            new CentroidClassifier(new FakeEmbeddings(dimension: 2), new CentroidClassifierOptions { Temperature = double.NaN }));
        Assert.Throws<ArgumentException>(() =>
            new CentroidClassifier(new FakeEmbeddings(dimension: 2), new CentroidClassifierOptions { Temperature = double.PositiveInfinity }));
    }

    [Fact]
    public void HashingEmbeddings_ExerciseTheNonBatchPathEndToEnd()
    {
        var classifier = new CentroidClassifier(new HashingEmbeddingProvider(dimension: 1024));
        classifier.Train(new[]
        {
            new SearchDocument("1", "invoice payment statement", Category: "Billing"),
            new SearchDocument("2", "server outage database error", Category: "Operations"),
        });

        Assert.Equal("Billing", classifier.PredictBest("invoice"));
        Assert.Equal("Operations", classifier.PredictBest("database"));
    }

    [Fact]
    public void TheGenericOptionsAreValidated()
    {
        Assert.Throws<ArgumentException>(() => new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category,
            new CentroidClassifierOptions { Temperature = 0 }));
    }

    [Fact]
    public void Train_RejectsANullItem()
    {
        var classifier = new CentroidClassifier<string>(
            _ => new[] { 1f, 0f },
            _ => "A");

        Assert.Throws<ArgumentNullException>(() => classifier.Train(new string[] { null! }));
    }

    [Fact]
    public void Train_SkipsAnEmptyVector()
    {
        var classifier = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category);

        classifier.Train(new[]
        {
            ("A", new[] { 1f, 0f }),
            ("B", Array.Empty<float>()),
        });

        Assert.Equal(1, classifier.CategoryCount);
        Assert.Empty(classifier.Predict(Array.Empty<float>()));
        Assert.Equal("A", classifier.PredictBest(new[] { 1f, 0f }));
    }

    [Fact]
    public void Learn_IgnoresACategorylessItem()
    {
        var classifier = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category);

        classifier.Learn(("", new[] { 1f, 0f }));

        Assert.Equal(0, classifier.CategoryCount);
        Assert.Empty(classifier.Predict(new[] { 1f, 0f }));
    }

    [Fact]
    public void Learn_SkipsAnEmptyVectorAndRejectsADimensionMismatch()
    {
        var classifier = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category);
        classifier.Train(new[] { ("A", new[] { 1f, 0f }) });

        classifier.Learn(("B", Array.Empty<float>()));
        Assert.Equal(1, classifier.CategoryCount);

        Assert.Throws<ArgumentException>(() => classifier.Learn(("B", new[] { 1f, 0f, 0f })));
    }

    [Fact]
    public void Unlearn_SkipsAnEmptyVector()
    {
        var classifier = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category);
        classifier.Train(new[] { ("A", new[] { 1f, 0f }) });

        classifier.Unlearn(("A", Array.Empty<float>()));

        Assert.Equal(1, classifier.CategoryCount);
    }

    [Fact]
    public void Unlearn_IgnoresACategorylessOrUnknownItem()
    {
        var classifier = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category);
        classifier.Train(new[] { ("A", new[] { 1f, 0f }) });

        classifier.Unlearn(("", new[] { 1f, 0f }));
        classifier.Unlearn(("Missing", new[] { 1f, 0f }));

        Assert.Equal(1, classifier.CategoryCount);
    }

    [Fact]
    public void Unlearn_RejectsADimensionMismatch()
    {
        var classifier = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category);
        classifier.Train(new[] { ("A", new[] { 1f, 0f }) });

        Assert.Throws<ArgumentException>(() => classifier.Unlearn(("A", new[] { 1f, 0f, 0f })));
    }

    [Fact]
    public void Unlearn_OneOfSeveralDocumentsKeepsTheCategory()
    {
        var classifier = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category);
        classifier.Train(new[]
        {
            ("A", new[] { 1f, 0f }),
            ("A", new[] { 0f, 1f }),
        });

        classifier.Unlearn(("A", new[] { 1f, 0f }));

        Assert.Equal(1, classifier.CategoryCount);
        Assert.Equal("A", classifier.PredictBest(new[] { 0.2f, 0.8f }));
    }

    [Fact]
    public void Unlearn_AllDocumentsClearsTheModel()
    {
        var classifier = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category);
        classifier.Train(new[]
        {
            ("A", new[] { 1f, 0f }),
            ("B", new[] { 0f, 1f }),
        });

        classifier.Unlearn(("A", new[] { 1f, 0f }));
        classifier.Unlearn(("B", new[] { 0f, 1f }));

        Assert.Equal(0, classifier.CategoryCount);
        Assert.Empty(classifier.Predict(new[] { 1f, 0f }));
    }

    [Fact]
    public void Learn_OnAFreshModelStartsTheModel()
    {
        var classifier = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category);

        classifier.Learn(("A", new[] { 1f, 0f }));

        Assert.Equal(1, classifier.CategoryCount);
        Assert.Equal("A", classifier.PredictBest(new[] { 1f, 0f }));
    }

    [Fact]
    public void LoadState_RejectsInconsistentState()
    {
        var classifier = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category);

        Assert.Throws<InvalidDataException>(() => classifier.LoadState(
            -1,
            new Dictionary<string, double[]>(),
            new Dictionary<string, int>(),
            Array.Empty<double>(),
            0));

        Assert.Throws<InvalidDataException>(() => classifier.LoadState(
            2,
            new Dictionary<string, double[]>(),
            new Dictionary<string, int> { ["A"] = 1 },
            new double[2],
            1));
    }

    [Fact]
    public async Task TrainAsync_WithNoLabelledDocumentLeavesTheModelEmpty()
    {
        var classifier = new CentroidClassifier(new FakeEmbeddings(dimension: 2));

        await classifier.TrainAsync(Array.Empty<SearchDocument>());

        Assert.Equal(0, classifier.CategoryCount);
    }

    [Fact]
    public async Task LearnAsync_IgnoresACategorylessDocument()
    {
        var embeddings = new FakeEmbeddings(dimension: 2);
        var classifier = new CentroidClassifier(embeddings);

        await classifier.LearnAsync(new SearchDocument("1", "text", Category: null));

        Assert.Equal(0, classifier.CategoryCount);
        Assert.Empty(embeddings.SingleCalls);
    }

    [Fact]
    public void TheCenteringFlagIsExposed()
    {
        var classifier = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category,
            new CentroidClassifierOptions { CenterOnGlobalMean = true });

        Assert.True(classifier.CenterOnGlobalMean);
    }

    [Fact]
    public async Task TheTextWrapper_SkipsATextWithoutAVector()
    {
        var embeddings = TwoClasses().Add("no vector", Array.Empty<float>());
        var classifier = new CentroidClassifier(embeddings);

        await classifier.TrainAsync(new[]
        {
            new SearchDocument("1", "A one", Category: "A"),
            new SearchDocument("2", "B one", Category: "B"),
            new SearchDocument("3", "no vector", Category: "C"),
        });

        Assert.Equal(2, classifier.CategoryCount);
        Assert.Empty(await classifier.PredictAsync("no vector"));

        await classifier.LearnAsync(new SearchDocument("4", "no vector", Category: "A"));
        Assert.Equal(2, classifier.CategoryCount);

        await classifier.UnlearnAsync(new SearchDocument("4", "no vector", Category: "A"));
        Assert.Equal(2, classifier.CategoryCount);
    }

    [Fact]
    public void TheTextWrapper_IsIncremental()
    {
        ITextClassifier classifier = new CentroidClassifier(TwoClasses());

        Assert.IsAssignableFrom<IIncrementalTextClassifier>(classifier);
    }

    [Fact]
    public void Learn_OneDocumentAtATime_EqualsTrainingTheCorpusAtOnce()
    {
        var trained = new CentroidClassifier(TwoClasses());
        trained.Train(TwoClassCorpus);

        var learned = new CentroidClassifier(TwoClasses());

        foreach (SearchDocument document in TwoClassCorpus)
            learned.Learn(document);

        foreach (string query in new[] { "A one", "A two", "B one", "B two", "A one B one" })
        {
            IReadOnlyList<ClassificationResult> expected = trained.Predict(query, limit: 10);
            IReadOnlyList<ClassificationResult> actual = learned.Predict(query, limit: 10);

            Assert.Equal(expected.Count, actual.Count);

            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Category, actual[i].Category);
                Assert.Equal(expected[i].Probability, actual[i].Probability, precision: 10);
            }
        }
    }

    [Fact]
    public void Learn_And_Unlearn_AreExactInverses()
    {
        var embeddings = TwoClasses().Add("C one", new[] { 0.5f, 0.5f });
        var classifier = new CentroidClassifier(embeddings);
        classifier.Train(TwoClassCorpus);

        string[] queries = { "A one", "B one", "A one B one" };
        var before = queries.ToDictionary(query => query, query => classifier.Predict(query, limit: 10));

        var addition = new SearchDocument("5", "C one", Category: "C");
        classifier.Learn(addition);
        classifier.Unlearn(addition);

        foreach (string query in queries)
        {
            IReadOnlyList<ClassificationResult> expected = before[query];
            IReadOnlyList<ClassificationResult> actual = classifier.Predict(query, limit: 10);

            Assert.Equal(expected.Count, actual.Count);

            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Category, actual[i].Category);
                Assert.Equal(expected[i].Probability, actual[i].Probability, precision: 10);
            }
        }
    }

    [Fact]
    public void Unlearn_RetiresACategoryThatHeldOneDocument()
    {
        var embeddings = TwoClasses().Add("C one", new[] { 0.5f, 0.5f });
        var classifier = new CentroidClassifier(embeddings);
        classifier.Train(TwoClassCorpus);

        var addition = new SearchDocument("5", "C one", Category: "C");
        classifier.Learn(addition);

        Assert.Equal(3, classifier.CategoryCount);

        classifier.Unlearn(addition);

        Assert.Equal(2, classifier.CategoryCount);
        Assert.NotEqual("C", classifier.PredictBest("C one"));
    }

    [Fact]
    public void Unlearn_OfAnUnknownCategory_SkipsTheProvider()
    {
        var embeddings = TwoClasses();
        var classifier = new CentroidClassifier(embeddings);
        classifier.Train(TwoClassCorpus);
        int calls = embeddings.SingleCalls.Count;

        classifier.Unlearn(new SearchDocument("9", "unknown", Category: "Missing"));

        Assert.Equal(calls, embeddings.SingleCalls.Count);
    }

    [Fact]
    public void TheGenericClassifier_LearnsAndUnlearns()
    {
        var classifier = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category);
        classifier.Train(new[]
        {
            ("A", new[] { 1f, 0f }),
            ("A", new[] { 0.9f, 0.1f }),
            ("B", new[] { 0f, 1f }),
            ("B", new[] { 0.1f, 0.9f }),
        });

        Assert.Equal("A", classifier.PredictBest(new[] { 1f, 0.1f }));

        classifier.Learn(("C", new[] { 1f, 1f }));
        Assert.Equal(3, classifier.CategoryCount);

        classifier.Unlearn(("C", new[] { 1f, 1f }));
        Assert.Equal(2, classifier.CategoryCount);
        Assert.Equal("A", classifier.PredictBest(new[] { 1f, 0.1f }));
    }

    [Fact]
    public void CenteringChangesTheRanking_WhenTheSharedComponentDominates()
    {
        // Hand-built so the raw cosine picks B and the centered cosine picks A: the query is
        // dominated by the corpus mean, and removing that mean exposes the class-specific part.
        var training = new[]
        {
            ("A", new[] { -1.9f, 0.17f }),
            ("A", new[] { 1.76f, -0.48f }),
            ("B", new[] { -1.13f, -0.31f }),
            ("B", new[] { -1.88f, -1.11f }),
        };
        float[] query = { -0.25f, -0.02f };

        var raw = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category);
        raw.Train(training);

        var centered = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category,
            new CentroidClassifierOptions { CenterOnGlobalMean = true });
        centered.Train(training);

        Assert.Equal("B", raw.PredictBest(query));
        Assert.Equal("A", centered.PredictBest(query));
    }

    [Fact]
    public void A_CenteredModel_StaysCenteredWhenItLearnsAndUnlearns()
    {
        var classifier = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category,
            new CentroidClassifierOptions { CenterOnGlobalMean = true });
        classifier.Train(new[]
        {
            ("A", new[] { -1.9f, 0.17f }),
            ("A", new[] { 1.76f, -0.48f }),
            ("B", new[] { -1.13f, -0.31f }),
            ("B", new[] { -1.88f, -1.11f }),
        });

        float[] query = { -0.25f, -0.02f };
        string? before = classifier.PredictBest(query);

        classifier.Learn(("C", new[] { 0.4f, 0.9f }));
        classifier.Unlearn(("C", new[] { 0.4f, 0.9f }));

        Assert.Equal(before, classifier.PredictBest(query));
    }

    [Fact]
    public async Task LearnAsync_MatchesTheBlockingCall()
    {
        var blocking = new CentroidClassifier(TwoClasses());
        blocking.Train(TwoClassCorpus);
        var asynchronous = new CentroidClassifier(TwoClasses());
        asynchronous.Train(TwoClassCorpus);

        var addition = new SearchDocument("5", "C one", Category: "C");
        var embeddings = TwoClasses().Add("C one", new[] { 0.5f, 0.5f });
        var first = new CentroidClassifier(embeddings);
        first.Train(TwoClassCorpus);
        var second = new CentroidClassifier(embeddings);
        second.Train(TwoClassCorpus);

        first.Learn(addition);
        await second.LearnAsync(addition);

        Assert.Equal(
            first.Predict("C one", limit: 10).Select(result => (result.Category, result.Probability)),
            second.Predict("C one", limit: 10).Select(result => (result.Category, result.Probability)));
    }

    // The published centroid table is rebuilt by the first reader after a mutation. Those readers
    // overlap, so this exercises the rebuild itself rather than the writer/readers split: each
    // round invalidates the table and then starts eight readers on the same query.
    [Fact]
    public void Predict_IsConsistentWhenReadersRebuildThePublishedState()
    {
        var classifier = new CentroidClassifier<SearchDocument>(
            document => document.Text switch
            {
                "left" => new[] { 1f, 0f },
                "right" => new[] { 0f, 1f },
                _ => new[] { 0.5f, 0.5f },
            },
            document => document.Category);

        classifier.Train(new[]
        {
            new SearchDocument("1", "left", Category: "A"),
            new SearchDocument("2", "right", Category: "B"),
        });

        ReadOnlyMemory<float> query = new[] { 1f, 0f };
        var errors = new ConcurrentQueue<Exception>();
        long reads = 0;

        for (int round = 0; round < 200; round++)
        {
            // Learn invalidates the published table, so this batch of readers races to rebuild it.
            classifier.Learn(new SearchDocument("m", "left", Category: "A"));

            Parallel.For(0, 8, _ =>
            {
                try
                {
                    Interlocked.Increment(ref reads);

                    if (classifier.PredictBest(query) != "A")
                        throw new InvalidOperationException("A reader observed a torn centroid table.");
                }
                catch (Exception failure)
                {
                    errors.Enqueue(failure);
                }
            });
        }

        Assert.Empty(errors);
        Assert.Equal(200 * 8, reads);
    }

    private sealed class FakeEmbeddings : IEmbeddingProvider, IBatchEmbeddingProvider
    {
        private readonly Dictionary<string, (float[] Query, float[] Passage)> _vectors =
            new(StringComparer.Ordinal);

        public FakeEmbeddings(int dimension) => Dimension = dimension;

        public int Dimension { get; }

        public List<(IReadOnlyList<string> Texts, EmbeddingUse Use)> BatchCalls { get; } = new();

        public List<(string Text, EmbeddingUse Use)> SingleCalls { get; } = new();

        public FakeEmbeddings Add(string text, float[] query, float[]? passage = null)
        {
            _vectors[text] = (query, passage ?? query);
            return this;
        }

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text,
            EmbeddingUse use,
            CancellationToken cancellationToken = default)
        {
            SingleCalls.Add((text, use));
            return Task.FromResult<ReadOnlyMemory<float>>(Vector(text, use));
        }

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GetTextEmbeddingsAsync(
            IReadOnlyList<string> texts,
            EmbeddingUse use,
            CancellationToken cancellationToken = default)
        {
            BatchCalls.Add((texts.ToArray(), use));

            IReadOnlyList<ReadOnlyMemory<float>> vectors = texts
                .Select(text => (ReadOnlyMemory<float>)Vector(text, use))
                .ToArray();

            return Task.FromResult(vectors);
        }

        private float[] Vector(string text, EmbeddingUse use)
        {
            var vector = _vectors[text];
            return use == EmbeddingUse.Query ? vector.Query : vector.Passage;
        }
    }

    private sealed class SingleOnlyEmbeddings : IEmbeddingProvider
    {
        private readonly FakeEmbeddings _inner;

        public SingleOnlyEmbeddings(FakeEmbeddings inner) => _inner = inner;

        public int Dimension => _inner.Dimension;

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text,
            EmbeddingUse use,
            CancellationToken cancellationToken = default) =>
            _inner.GetTextEmbeddingAsync(text, use, cancellationToken);
    }

    private sealed class ShortBatchEmbeddings : IEmbeddingProvider, IBatchEmbeddingProvider
    {
        public int Dimension => 2;

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text,
            EmbeddingUse use,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ReadOnlyMemory<float>>(new[] { 1f, 0f });

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GetTextEmbeddingsAsync(
            IReadOnlyList<string> texts,
            EmbeddingUse use,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ReadOnlyMemory<float>> one = new[] { (ReadOnlyMemory<float>)new[] { 1f, 0f } };
            return Task.FromResult(one);
        }
    }
}
