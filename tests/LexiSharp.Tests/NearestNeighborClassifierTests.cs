using LexiSharp.Classification;
using LexiSharp.Core;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class NearestNeighborClassifierTests
{
    private static readonly SearchDocument[] Corpus =
    {
        new("1", "alpha beta", Category: "A"),
        new("2", "alpha gamma", Category: "A"),
        new("3", "delta epsilon", Category: "B"),
        new("4", "delta zeta", Category: "B"),
    };

    private static readonly string[] Queries =
    {
        "alpha",
        "delta",
        "alpha delta",
        "alpha beta",
        "nothing matches",
    };

    [Fact]
    public void Bm25_PrefersTheDocumentThatSharesTheRareTerm()
    {
        var classifier = new NearestNeighborClassifier();
        classifier.Train(Corpus);

        Assert.Equal("A", classifier.PredictBest("alpha"));
        Assert.Equal("B", classifier.PredictBest("delta"));
    }

    [Fact]
    public void TheNeighbourCountChangesHowManyVotesAreCounted()
    {
        var classifier = new NearestNeighborClassifier(options: new NearestNeighborOptions { K = 1 });
        classifier.Train(Corpus);

        var one = classifier.Predict("alpha delta", limit: 10);

        var five = new NearestNeighborClassifier(options: new NearestNeighborOptions { K = 5 });
        five.Train(Corpus);

        var all = five.Predict("alpha delta", limit: 10);

        Assert.Single(one);
        Assert.Equal(2, all.Count);
        Assert.Equal(1.0, all.Sum(result => result.Probability), precision: 10);
    }

    [Fact]
    public void ExclusionsAreAppliedBeforeTheTopKCut()
    {
        var classifier = new NearestNeighborClassifier(options: new NearestNeighborOptions { K = 1 });
        classifier.Train(Corpus);

        var results = classifier.Predict("alpha delta", limit: 1, new HashSet<string>(new[] { "A" }));

        Assert.Single(results);
        Assert.Equal("B", results[0].Category);
    }

    [Fact]
    public void LearnOneByOne_EqualsTrainingTheCorpusAtOnce()
    {
        foreach ((string name, NearestNeighborOptions options) in Combinations())
        {
            var trained = new NearestNeighborClassifier(options: options);
            trained.Train(Corpus);

            var learned = new NearestNeighborClassifier(options: options);

            foreach (SearchDocument document in Corpus)
                learned.Learn(document);

            foreach (string query in Queries)
            {
                var expected = trained.Predict(query, limit: 10);
                var actual = learned.Predict(query, limit: 10);

                Assert.Equal(
                    expected.Select(result => (result.Category, result.Probability)),
                    actual.Select(result => (result.Category, result.Probability)));
            }

            Assert.True(true, name);
        }
    }

    private static IEnumerable<(string Name, NearestNeighborOptions Options)> Combinations()
    {
        yield return ("defaults", new NearestNeighborOptions());
        yield return ("k=1", new NearestNeighborOptions { K = 1 });
        yield return ("k=10", new NearestNeighborOptions { K = 10 });
        yield return ("rank", new NearestNeighborOptions { Vote = NeighborVote.Rank });
        yield return ("temperature", new NearestNeighborOptions { Vote = NeighborVote.Temperature });
        yield return ("conservative-bm25", new NearestNeighborOptions { Bm25 = Bm25Parameters.Conservative });
        yield return ("aggressive-bm25", new NearestNeighborOptions { Bm25 = Bm25Parameters.Aggressive });
        yield return ("abstain", new NearestNeighborOptions { MinProbability = 0.99 });
    }

    [Fact]
    public void LearnAndUnlearnAreExactInverses()
    {
        var classifier = new NearestNeighborClassifier();
        classifier.Train(Corpus);

        var before = Queries.ToDictionary(query => query, query => classifier.Predict(query, limit: 10));

        var addition = new SearchDocument("5", "alpha delta", Category: "C");
        classifier.Learn(addition);
        classifier.Unlearn(addition);

        foreach (string query in Queries)
        {
            Assert.Equal(
                before[query].Select(result => (result.Category, result.Probability)),
                classifier.Predict(query, limit: 10).Select(result => (result.Category, result.Probability)));
        }
    }

    [Fact]
    public void ADuplicateIdIsRejected()
    {
        var classifier = new NearestNeighborClassifier();
        classifier.Learn(new SearchDocument("1", "alpha", Category: "A"));

        Assert.Throws<ArgumentException>(() =>
            classifier.Learn(new SearchDocument("1", "beta", Category: "B")));
        Assert.Throws<ArgumentException>(() =>
            classifier.Train(new[]
            {
                new SearchDocument("1", "alpha", Category: "A"),
                new SearchDocument("1", "beta", Category: "B"),
            }));
    }

    [Fact]
    public void UnlearnOfAnUnknownIdIsANoOp()
    {
        var classifier = new NearestNeighborClassifier();
        classifier.Train(Corpus);

        classifier.Unlearn(new SearchDocument("missing", "alpha", Category: "A"));

        Assert.Equal(4, classifier.Count);
    }

    [Fact]
    public void InvalidOptionsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new NearestNeighborClassifier(options: new NearestNeighborOptions { K = -1 }));
        Assert.Throws<ArgumentException>(() => new NearestNeighborClassifier(options: new NearestNeighborOptions { Temperature = 0 }));
        Assert.Throws<ArgumentException>(() => new NearestNeighborClassifier(options: new NearestNeighborOptions { MinProbability = 2 }));
        Assert.Throws<ArgumentException>(() => new NearestNeighborClassifier(options: new NearestNeighborOptions
        {
            Bm25 = new Bm25Parameters(1, 2),
        }));
    }

    [Fact]
    public void UntrainedAndKZeroReturnNothing()
    {
        Assert.Empty(new NearestNeighborClassifier().Predict("alpha"));

        var classifier = new NearestNeighborClassifier(options: new NearestNeighborOptions { K = 0 });
        classifier.Train(Corpus);
        Assert.Empty(classifier.Predict("alpha"));
    }

    [Fact]
    public void EmptyTextReturnsNothing()
    {
        var classifier = new NearestNeighborClassifier();
        classifier.Train(Corpus);

        Assert.Empty(classifier.Predict(string.Empty));
    }

    [Fact]
    public void DenseKnn_RanksByCosineAndVotes()
    {
        var embeddings = new FakeEmbeddings()
            .Add("A", new[] { 1f, 0f })
            .Add("B", new[] { 0.2f, 0.9797959f })
            .Add("query", new[] { 1f, 0f });

        var classifier = new NearestNeighborClassifier(embeddings, new NearestNeighborOptions
        {
            K = 2,
            Vote = NeighborVote.Score,
        });
        classifier.Train(new[]
        {
            new SearchDocument("a", "A", Category: "A"),
            new SearchDocument("b", "B", Category: "B"),
        });

        var results = classifier.Predict("query", limit: 2);

        Assert.Equal("A", results[0].Category);
        Assert.Equal(2, results.Count);
        Assert.Equal(1.0, results.Sum(result => result.Probability), precision: 10);
        Assert.True(results.Single(result => result.Category == "A").Probability > 0.8);
    }

    [Fact]
    public void DenseVoteModesProduceDifferentMixes()
    {
        var embeddings = new FakeEmbeddings()
            .Add("A", new[] { 1f, 0f })
            .Add("B", new[] { 0.2f, 0.9797959f })
            .Add("query", new[] { 1f, 0f });

        double Score(NeighborVote vote, double temperature = 0.1)
        {
            var classifier = new NearestNeighborClassifier(embeddings, new NearestNeighborOptions
            {
                K = 2,
                Vote = vote,
                Temperature = temperature,
            });
            classifier.Train(new[]
            {
                new SearchDocument("a", "A", Category: "A"),
                new SearchDocument("b", "B", Category: "B"),
            });

            return classifier.Predict("query", limit: 2)
                .Single(result => result.Category == "A").Probability;
        }

        double score = Score(NeighborVote.Score);
        double rank = Score(NeighborVote.Rank);
        double temperature = Score(NeighborVote.Temperature);

        Assert.True(score > rank, $"score {score} should exceed rank {rank}");
        Assert.True(temperature > score, $"temperature {temperature} should exceed score {score}");
    }

    [Fact]
    public void DenseLearnAndUnlearnAreExactInverses()
    {
        var embeddings = new FakeEmbeddings()
            .Add("A", new[] { 1f, 0f })
            .Add("B", new[] { 0f, 1f })
            .Add("C", new[] { 0.7f, 0.7f })
            .Add("query", new[] { 1f, 0f });

        var classifier = new NearestNeighborClassifier(embeddings, new NearestNeighborOptions { K = 2 });
        classifier.Train(new[]
        {
            new SearchDocument("a", "A", Category: "A"),
            new SearchDocument("b", "B", Category: "B"),
        });

        var before = classifier.Predict("query", limit: 2);
        var addition = new SearchDocument("c", "C", Category: "C");
        classifier.Learn(addition);
        classifier.Unlearn(addition);

        Assert.Equal(
            before.Select(result => (result.Category, result.Probability)),
            classifier.Predict("query", limit: 2).Select(result => (result.Category, result.Probability)));
    }

    [Fact]
    public async Task DenseAsyncMatchesTheBlockingCall()
    {
        var embeddings = new FakeEmbeddings()
            .Add("A", new[] { 1f, 0f })
            .Add("B", new[] { 0f, 1f })
            .Add("query", new[] { 1f, 0f });

        var blocking = new NearestNeighborClassifier(embeddings);
        blocking.Train(new[]
        {
            new SearchDocument("a", "A", Category: "A"),
            new SearchDocument("b", "B", Category: "B"),
        });

        var asynchronous = new NearestNeighborClassifier(embeddings);
        await asynchronous.TrainAsync(new[]
        {
            new SearchDocument("a", "A", Category: "A"),
            new SearchDocument("b", "B", Category: "B"),
        });

        Assert.Equal(
            blocking.Predict("query", limit: 2).Select(result => (result.Category, result.Probability)),
            (await asynchronous.PredictAsync("query", limit: 2))
                .Select(result => (result.Category, result.Probability)));
    }

    [Fact]
    public void DenseTiesAreOrderedByCategoryName()
    {
        var embeddings = new FakeEmbeddings()
            .Add("B", new[] { 1f, 0f })
            .Add("A", new[] { 1f, 0f })
            .Add("query", new[] { 1f, 0f });

        var classifier = new NearestNeighborClassifier(embeddings, new NearestNeighborOptions
        {
            K = 2,
            Vote = NeighborVote.Score,
        });
        classifier.Train(new[]
        {
            new SearchDocument("b", "B", Category: "B"),
            new SearchDocument("a", "A", Category: "A"),
        });

        var results = classifier.Predict("query", limit: 2);

        Assert.Equal(new[] { "A", "B" }, results.Select(result => result.Category));
        Assert.Equal(results[0].Probability, results[1].Probability, precision: 10);
    }

    [Fact]
    public void DenseSkipsATextWithoutAVector()
    {
        var embeddings = new FakeEmbeddings()
            .Add("A", new[] { 1f, 0f })
            .Add("empty", Array.Empty<float>());

        var classifier = new NearestNeighborClassifier(embeddings);
        classifier.Train(new[]
        {
            new SearchDocument("a", "A", Category: "A"),
            new SearchDocument("empty", "empty", Category: "B"),
        });

        Assert.Equal(1, classifier.Count);
        Assert.Empty(classifier.Predict("empty"));

        classifier.Learn(new SearchDocument("empty-2", "empty", Category: "B"));
        Assert.Equal(1, classifier.Count);
    }

    [Fact]
    public async Task DenseBatchSkipsATextWithoutAVector()
    {
        var embeddings = new BatchEmbeddings()
            .Add("A", new[] { 1f, 0f })
            .Add("empty", Array.Empty<float>());

        var classifier = new NearestNeighborClassifier(embeddings);
        await classifier.TrainAsync(new[]
        {
            new SearchDocument("a", "A", Category: "A"),
            new SearchDocument("empty", "empty", Category: "B"),
        });

        Assert.Equal(1, classifier.Count);
    }

    [Fact]
    public void DenseBadVectorIsRejected()
    {
        var embeddings = new FakeEmbeddings(dimension: 3)
            .Add("bad", new[] { 1f, 0f });

        var classifier = new NearestNeighborClassifier(embeddings);

        Assert.Throws<InvalidOperationException>(() => classifier.Learn(
            new SearchDocument("1", "bad", Category: "A")));
    }

    [Fact]
    public void TheConfigurationIsExposed()
    {
        var lexical = new NearestNeighborClassifier(options: new NearestNeighborOptions { K = 7 });
        Assert.Equal(7, lexical.Options.K);
        Assert.False(lexical.IsDense);
        Assert.Equal(0, lexical.Count);

        var dense = new NearestNeighborClassifier(new FakeEmbeddings());
        Assert.True(dense.IsDense);
    }

    [Fact]
    public void TheDenseConstructorValidatesItsOptions()
    {
        Assert.Throws<ArgumentException>(() => new NearestNeighborClassifier(
            new FakeEmbeddings(),
            new NearestNeighborOptions { K = -1 }));
    }

    [Fact]
    public async Task TrainAsync_OnTheLexicalPath_DelegatesToTrain()
    {
        var classifier = new NearestNeighborClassifier();

        await classifier.TrainAsync(Corpus);

        Assert.Equal(4, classifier.Count);
        Assert.Equal("A", classifier.PredictBest("alpha"));
    }

    [Fact]
    public async Task LearnAsync_OnTheLexicalPath_DelegatesToLearn()
    {
        var classifier = new NearestNeighborClassifier();

        await classifier.LearnAsync(new SearchDocument("1", "alpha", Category: "A"));

        Assert.Equal(1, classifier.Count);
        Assert.Equal("A", classifier.PredictBest("alpha"));
    }

    [Fact]
    public async Task DenseTrainAsync_UsesTheBatchProviderOnce()
    {
        var embeddings = new BatchEmbeddings()
            .Add("A", new[] { 1f, 0f })
            .Add("B", new[] { 0f, 1f });

        var classifier = new NearestNeighborClassifier(embeddings);
        await classifier.TrainAsync(new[]
        {
            new SearchDocument("a", "A", Category: "A"),
            new SearchDocument("b", "B", Category: "B"),
        });

        Assert.Equal(1, embeddings.BatchCalls);
        Assert.Equal(2, classifier.Count);
    }

    [Fact]
    public async Task DenseTrainAsync_RejectsABatchCountMismatch()
    {
        var embeddings = new BatchEmbeddings { ReturnOne = true }
            .Add("A", new[] { 1f, 0f })
            .Add("B", new[] { 0f, 1f });

        var classifier = new NearestNeighborClassifier(embeddings);

        await Assert.ThrowsAsync<InvalidOperationException>(() => classifier.TrainAsync(new[]
        {
            new SearchDocument("a", "A", Category: "A"),
            new SearchDocument("b", "B", Category: "B"),
        }));
    }

    [Fact]
    public void LearnAndUnlearnIgnoreADocumentWithoutCategory()
    {
        var classifier = new NearestNeighborClassifier();
        var document = new SearchDocument("1", "alpha", Category: null);

        classifier.Learn(document);
        classifier.Unlearn(document);

        Assert.Equal(0, classifier.Count);
    }

    [Fact]
    public async Task LearnAsyncAndUnlearnAsyncIgnoreADocumentWithoutCategory()
    {
        var embeddings = new FakeEmbeddings().Add("alpha", new[] { 1f, 0f });
        var classifier = new NearestNeighborClassifier(embeddings);
        var document = new SearchDocument("1", "alpha", Category: null);

        await classifier.LearnAsync(document);
        await classifier.UnlearnAsync(document);

        Assert.Equal(0, classifier.Count);
    }

    [Fact]
    public async Task DensePredictAsync_ReturnsNothingBeforeTraining()
    {
        var embeddings = new FakeEmbeddings();
        var classifier = new NearestNeighborClassifier(embeddings);

        Assert.Empty(await classifier.PredictAsync("anything"));
        Assert.Equal(0, embeddings.Calls);
        Assert.Empty(await classifier.PredictAsync("anything", limit: 0));
    }

    [Fact]
    public async Task LexicalPredictAsync_DelegatesToTheSynchronousPath()
    {
        var classifier = new NearestNeighborClassifier();
        classifier.Train(Corpus);

        Assert.Equal("A", (await classifier.PredictAsync("alpha", limit: 1))[0].Category);
    }

    [Fact]
    public void DenseScoreVote_WithAllNegativeSimilarities_IsUniform()
    {
        var embeddings = new FakeEmbeddings()
            .Add("A", new[] { 1f, 0f })
            .Add("B", new[] { 0f, 1f })
            .Add("query", new[] { -1f, -1f });

        var classifier = new NearestNeighborClassifier(embeddings, new NearestNeighborOptions
        {
            K = 2,
            Vote = NeighborVote.Score,
        });
        classifier.Train(new[]
        {
            new SearchDocument("a", "A", Category: "A"),
            new SearchDocument("b", "B", Category: "B"),
        });

        var results = classifier.Predict("query", limit: 2);

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.Equal(0.5, result.Probability, precision: 10));
    }

    [Fact]
    public void ADenseNonFiniteVectorIsRejected()
    {
        var embeddings = new FakeEmbeddings()
            .Add("bad", new[] { float.NaN, 0f });

        var classifier = new NearestNeighborClassifier(embeddings);

        Assert.Throws<InvalidOperationException>(() => classifier.Learn(
            new SearchDocument("1", "bad", Category: "A")));
    }

    [Fact]
    public void TheTopKBufferReplacesTheWorstCandidate()
    {
        var classifier = new NearestNeighborClassifier(options: new NearestNeighborOptions { K = 1 });
        classifier.Learn(new SearchDocument("weak", "alpha", Category: "A"));
        classifier.Learn(new SearchDocument("strong", "alpha alpha alpha alpha", Category: "B"));

        Assert.Equal("B", classifier.PredictBest("alpha"));
    }

    [Fact]
    public void AnEmptyIdIsRejectedByBothStores()
    {
        var lexical = new NearestNeighborClassifier();
        Assert.Throws<ArgumentException>(() =>
            lexical.Learn(new SearchDocument(string.Empty, "alpha", Category: "A")));

        var embeddings = new FakeEmbeddings().Add("alpha", new[] { 1f, 0f });
        var dense = new NearestNeighborClassifier(embeddings);
        Assert.Throws<ArgumentException>(() =>
            dense.Learn(new SearchDocument(string.Empty, "alpha", Category: "A")));
    }

    [Fact]
    public void ADuplicateIdIsRejectedByTheDenseStore()
    {
        var embeddings = new FakeEmbeddings()
            .Add("one", new[] { 1f, 0f })
            .Add("two", new[] { 0f, 1f });

        var classifier = new NearestNeighborClassifier(embeddings);
        classifier.Learn(new SearchDocument("1", "one", Category: "A"));

        Assert.Throws<ArgumentException>(() =>
            classifier.Learn(new SearchDocument("1", "two", Category: "B")));
    }

    [Fact]
    public void UnlearningAUniqueTermRemovesItFromTheDocumentFrequency()
    {
        var classifier = new NearestNeighborClassifier();
        classifier.Train(new[]
        {
            new SearchDocument("1", "unique alpha", Category: "A"),
            new SearchDocument("2", "beta", Category: "B"),
        });

        classifier.Unlearn(new SearchDocument("1", "unique alpha", Category: "A"));

        Assert.Empty(classifier.Predict("unique"));
    }

    [Fact]
    public void AnEmptyModelAndAnEmptyTextReturnNothing()
    {
        var emptyModel = new NearestNeighborClassifier();
        emptyModel.Train(Array.Empty<SearchDocument>());
        Assert.Empty(emptyModel.Predict("alpha"));

        var emptyDocuments = new NearestNeighborClassifier();
        emptyDocuments.Train(new[] { new SearchDocument("1", string.Empty, Category: "A") });
        Assert.Empty(emptyDocuments.Predict("alpha"));
    }

    private sealed class BatchEmbeddings : IEmbeddingProvider, IBatchEmbeddingProvider
    {
        private readonly Dictionary<string, float[]> _vectors = new(StringComparer.Ordinal);

        public int Dimension => 2;
        public int BatchCalls { get; private set; }
        public bool ReturnOne { get; init; }

        public BatchEmbeddings Add(string text, float[] vector)
        {
            _vectors[text] = vector;
            return this;
        }

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text,
            EmbeddingUse use,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ReadOnlyMemory<float>>(_vectors[text]);

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GetTextEmbeddingsAsync(
            IReadOnlyList<string> texts,
            EmbeddingUse use,
            CancellationToken cancellationToken = default)
        {
            BatchCalls++;
            IReadOnlyList<ReadOnlyMemory<float>> vectors = texts
                .Take(ReturnOne ? 1 : texts.Count)
                .Select(text => (ReadOnlyMemory<float>)_vectors[text])
                .ToArray();

            return Task.FromResult(vectors);
        }
    }

    private sealed class FakeEmbeddings : IEmbeddingProvider
    {
        private readonly Dictionary<string, float[]> _vectors = new(StringComparer.Ordinal);

        public FakeEmbeddings(int dimension = 2) => Dimension = dimension;

        public int Dimension { get; }

        public int Calls { get; private set; }

        public FakeEmbeddings Add(string text, float[] vector)
        {
            _vectors[text] = vector;
            return this;
        }

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text,
            EmbeddingUse use,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<ReadOnlyMemory<float>>(_vectors[text]);
        }
    }
}
