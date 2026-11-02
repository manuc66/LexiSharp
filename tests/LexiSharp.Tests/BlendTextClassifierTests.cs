using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class BlendTextClassifierTests
{
    private static readonly SearchDocument[] Corpus =
    {
        new("1", "first document", Category: "A"),
        new("2", "second document", Category: "B"),
    };

    [Fact]
    public void LearnAndUnlearnReachEveryLane()
    {
        var first = new IncrementalClassifier();
        var second = new IncrementalClassifier();
        var blend = new BlendTextClassifier(new[]
        {
            new TextClassifierLane(first, 0.5),
            new TextClassifierLane(second, 0.5),
        });
        var document = new SearchDocument("1", "text", Category: "A");

        blend.Learn(document);
        Assert.Same(document, Assert.Single(first.Learned));
        Assert.Same(document, Assert.Single(second.Learned));

        blend.Unlearn(document);
        Assert.Same(document, Assert.Single(first.Unlearned));
        Assert.Same(document, Assert.Single(second.Unlearned));
    }

    [Fact]
    public void A_WrappedNonIncrementalLaneDoesNotCountAsIncremental()
    {
        using var lane = new SynchronizedTextClassifier(
            new FixedClassifier(new ClassificationResult("A", 1)));
        var blend = new BlendTextClassifier(new[]
        {
            new TextClassifierLane(lane, 1),
        });

        Assert.False(blend.SupportsIncremental);
        Assert.Throws<NotSupportedException>(() =>
            blend.Learn(new SearchDocument("1", "text", Category: "A")));
    }

    [Fact]
    public void LearnRefusesANonIncrementalLaneBeforeMutatingAnyLane()
    {
        var incremental = new IncrementalClassifier();
        var blend = new BlendTextClassifier(new[]
        {
            new TextClassifierLane(incremental, 0.5),
            new TextClassifierLane(new FixedClassifier(new ClassificationResult("B", 1)), 0.5),
        });

        Assert.Throws<NotSupportedException>(() =>
            blend.Learn(new SearchDocument("1", "text", Category: "A")));
        Assert.Empty(incremental.Learned);
    }

    [Fact]
    public void FusedForwardsLearnAndUnlearn()
    {
        var first = new IncrementalClassifier();
        var second = new IncrementalClassifier();
        var fused = new FusedTextClassifier(first, second);
        var document = new SearchDocument("1", "text", Category: "A");

        fused.Learn(document);
        fused.Unlearn(document);

        Assert.Same(document, Assert.Single(first.Learned));
        Assert.Same(document, Assert.Single(second.Learned));
        Assert.Same(document, Assert.Single(first.Unlearned));
        Assert.Same(document, Assert.Single(second.Unlearned));
    }

    [Fact]
    public void ARequestContextCanIntroduceACandidateWithoutLeakingIntoTheNextCall()
    {
        var blend = new BlendTextClassifier(new[]
        {
            new TextClassifierLane(new FixedClassifier(
                new ClassificationResult("A", 0.6), new ClassificationResult("B", 0.4)), 1),
        });
        var context = new BlendContext
        {
            Candidates = new[] { new TextEvidence("X", 2) },
        };

        var withContext = blend.Predict("anything", context, limit: 3);
        var withoutContext = blend.Predict("anything", limit: 3);

        Assert.Equal("X", withContext[0].Category);
        Assert.Equal("A", withoutContext[0].Category);
        Assert.DoesNotContain(withoutContext, result => result.Category == "X");
    }

    [Fact]
    public void ARequestContextCanApplyAPrior()
    {
        var blend = new BlendTextClassifier(new[]
        {
            new TextClassifierLane(new FixedClassifier(
                new ClassificationResult("A", 0.5), new ClassificationResult("B", 0.5)), 1),
        });
        var context = new BlendContext
        {
            Priors = new[] { new TextEvidence("A", Math.Log(2)) },
        };

        var results = blend.Predict("anything", context, limit: 2);

        Assert.Equal(2.0 / 3.0, results[0].Probability, precision: 10);
    }

    [Fact]
    public void AnEmptyContextMatchesNoContext()
    {
        var blend = new BlendTextClassifier(new[]
        {
            new TextClassifierLane(new FixedClassifier(
                new ClassificationResult("A", 0.7), new ClassificationResult("B", 0.3)), 1),
        });

        Assert.Equal(
            blend.Predict("anything", limit: 2).Select(result => (result.Category, result.Probability)),
            blend.Predict("anything", BlendContext.Empty, limit: 2)
                .Select(result => (result.Category, result.Probability)));
    }

    [Fact]
    public void TheRequestContextIsValidated()
    {
        var blend = new BlendTextClassifier(new[]
        {
            new TextClassifierLane(new FixedClassifier(new ClassificationResult("A", 1)), 1),
        });

        Assert.Throws<ArgumentNullException>(() => blend.Predict("anything", (BlendContext)null!));
        Assert.Throws<ArgumentException>(() => blend.Predict(
            "anything", new BlendContext { CandidateWeight = -1 }));
        Assert.Throws<ArgumentException>(() => blend.Predict(
            "anything", new BlendContext { PriorWeight = double.NaN }));
        Assert.Throws<ArgumentException>(() => blend.Predict(
            "anything",
            new BlendContext { Candidates = new[] { new TextEvidence(string.Empty, 1) } }));
        Assert.Throws<ArgumentException>(() => blend.Predict(
            "anything",
            new BlendContext { Priors = new[] { new TextEvidence("A", double.NaN) } }));
    }

    [Fact]
    public void PredictBestAcceptsARequestContext()
    {
        var blend = new BlendTextClassifier(new[]
        {
            new TextClassifierLane(new FixedClassifier(
                new ClassificationResult("A", 0.5), new ClassificationResult("B", 0.5)), 1),
        });

        Assert.Equal("X", blend.PredictBestWithContext(
            "anything",
            new BlendContext { Candidates = new[] { new TextEvidence("X", 10) } }));
    }

    [Fact]
    public void TheLanesAndEvidenceAreExposed()
    {
        var lane = new TextClassifierLane(new FixedClassifier(new ClassificationResult("A", 1)), 1);
        var candidate = new CandidateLane(new FixedCandidateSource(new TextEvidence("X", 1)), 1);
        var prior = new PriorLane(new FixedPriorSource(new TextEvidence("A", 0)), 1);

        var blend = new BlendTextClassifier(new[] { lane }, new[] { candidate }, new[] { prior });

        Assert.Same(lane, Assert.Single(blend.Lanes));
        Assert.Same(candidate, Assert.Single(blend.Candidates));
        Assert.Same(prior, Assert.Single(blend.Priors));
    }

    [Fact]
    public void TheConstructorValidatesItsLanesAndEvidence()
    {
        Assert.Throws<ArgumentNullException>(() => new BlendTextClassifier(null!));
        Assert.Throws<ArgumentNullException>(() => new BlendTextClassifier(new TextClassifierLane[]
        {
            null!,
        }));
        Assert.Throws<ArgumentNullException>(() => new BlendTextClassifier(new[]
        {
            new TextClassifierLane(null!, 1),
        }));
        Assert.Throws<ArgumentNullException>(() => new BlendTextClassifier(
            new[] { new TextClassifierLane(new FixedClassifier(new ClassificationResult("A", 1)), 1) },
            new[] { new CandidateLane(null!, 1) }));
        Assert.Throws<ArgumentNullException>(() => new BlendTextClassifier(
            new[] { new TextClassifierLane(new FixedClassifier(new ClassificationResult("A", 1)), 1) },
            priors: new[] { new PriorLane(null!, 1) }));
        Assert.Throws<ArgumentException>(() => new BlendTextClassifier(
            new[] { new TextClassifierLane(new FixedClassifier(new ClassificationResult("A", 1)), 1) },
            new[] { new CandidateLane(new FixedCandidateSource(new TextEvidence("X", 1)), -1) }));
        Assert.Throws<ArgumentException>(() => new BlendTextClassifier(
            new[] { new TextClassifierLane(new FixedClassifier(new ClassificationResult("A", 1)), 1) },
            priors: new[] { new PriorLane(new FixedPriorSource(new TextEvidence("A", 0)), -1) }));
    }

    [Fact]
    public void A_ZeroMassDistributionReturnsNothing()
    {
        var blend = new BlendTextClassifier(new[]
        {
            new TextClassifierLane(new FixedClassifier(new ClassificationResult("A", 0)), 1),
        });

        Assert.Empty(blend.Predict("anything"));
    }

    [Fact]
    public void A_PriorOnAZeroMassDistributionStillReturnsNothing()
    {
        var blend = new BlendTextClassifier(
            new[] { new TextClassifierLane(new FixedClassifier(new ClassificationResult("A", 0)), 1) },
            priors: new[] { new PriorLane(new FixedPriorSource(new TextEvidence("A", 5)), 1) });

        Assert.Empty(blend.Predict("anything"));
    }

    [Fact]
    public void A_ZeroProbabilityCategoryStaysAtZeroAfterAPrior()
    {
        var blend = new BlendTextClassifier(
            new[]
            {
                new TextClassifierLane(new FixedClassifier(
                    new ClassificationResult("A", 1), new ClassificationResult("B", 0)), 1),
            },
            priors: new[] { new PriorLane(new FixedPriorSource(new TextEvidence("A", 1)), 1) });

        var results = blend.Predict("anything", limit: 2);

        Assert.Equal("A", results[0].Category);
        Assert.Equal(0, results.Single(result => result.Category == "B").Probability);
    }

    [Fact]
    public void ExcludingEveryCategoryReturnsNothing()
    {
        var blend = new BlendTextClassifier(new[]
        {
            new TextClassifierLane(new FixedClassifier(
                new ClassificationResult("A", 0.5), new ClassificationResult("B", 0.5)), 1),
        });

        Assert.Empty(blend.Predict("anything", excludedCategories: new HashSet<string>(new[] { "A", "B" })));
    }

    [Fact]
    public void EvidenceWithAnEmptyCategoryIsRejected()
    {
        var blend = new BlendTextClassifier(
            new[] { new TextClassifierLane(new FixedClassifier(new ClassificationResult("A", 1)), 1) },
            new[] { new CandidateLane(new FixedCandidateSource(new TextEvidence(string.Empty, 1)), 1) });

        Assert.Throws<ArgumentException>(() => blend.Predict("anything"));
    }

    [Fact]
    public void ANonFinitePriorIsRejected()
    {
        var blend = new BlendTextClassifier(
            new[] { new TextClassifierLane(new FixedClassifier(new ClassificationResult("A", 1)), 1) },
            priors: new[] { new PriorLane(new FixedPriorSource(new TextEvidence("A", double.NaN)), 1) });

        Assert.Throws<ArgumentException>(() => blend.Predict("anything"));
    }

    [Fact]
    public void A_ThreeLaneBlendSumsTheWeightedDistributions()
    {
        var blend = new BlendTextClassifier(new[]
        {
            new TextClassifierLane(new FixedClassifier(
                new ClassificationResult("A", 0.6), new ClassificationResult("B", 0.4)), 0.5),
            new TextClassifierLane(new FixedClassifier(
                new ClassificationResult("A", 0.5), new ClassificationResult("C", 0.5)), 0.3),
            new TextClassifierLane(new FixedClassifier(
                new ClassificationResult("B", 0.8), new ClassificationResult("C", 0.2)), 0.2),
        });

        var results = blend.Predict("anything", limit: 3);

        Assert.Equal(new[] { "A", "B", "C" }, results.Select(result => result.Category));
        Assert.Equal(0.45, results[0].Probability, precision: 10);
        Assert.Equal(0.36, results[1].Probability, precision: 10);
        Assert.Equal(0.19, results[2].Probability, precision: 10);
        Assert.Equal(1.0, results.Sum(result => result.Probability), precision: 10);
    }

    [Fact]
    public void ACandidateCanIntroduceACategoryNoModelLearned()
    {
        var blend = new BlendTextClassifier(
            new[]
            {
                new TextClassifierLane(new FixedClassifier(
                    new ClassificationResult("A", 0.6), new ClassificationResult("B", 0.4)), 1),
            },
            new[]
            {
                new CandidateLane(new FixedCandidateSource(new TextEvidence("PROJ-42", 2)), 1),
            });

        var results = blend.Predict("see PROJ-42", limit: 3);

        Assert.Equal("PROJ-42", results[0].Category);
        Assert.Equal(0.5, results[0].Probability, precision: 10);
        Assert.Equal(0.3, results[1].Probability, precision: 10);
        Assert.Equal(0.2, results[2].Probability, precision: 10);
    }

    [Fact]
    public void CandidateScoresAreNormalizedPerSource()
    {
        var blend = new BlendTextClassifier(
            new[]
            {
                new TextClassifierLane(new FixedClassifier(new ClassificationResult("A", 1.0)), 1),
            },
            new[]
            {
                new CandidateLane(
                    new FixedCandidateSource(new TextEvidence("X", 3), new TextEvidence("Y", 1)),
                    0.5),
            });

        var results = blend.Predict("anything", limit: 3);

        Assert.Equal(1.5, 1.0 + 0.5, precision: 10); // total mass: A plus the candidate lane
        Assert.Equal(2.0 / 3.0, results.Single(result => result.Category == "A").Probability, precision: 10);
        Assert.Equal(0.25, results.Single(result => result.Category == "X").Probability, precision: 10);
        Assert.Equal(1.0 / 12.0, results.Single(result => result.Category == "Y").Probability, precision: 10);
    }

    [Fact]
    public void APriorShiftsAnExistingCategoryWithoutCreatingOne()
    {
        var blend = new BlendTextClassifier(
            new[]
            {
                new TextClassifierLane(new FixedClassifier(
                    new ClassificationResult("A", 0.5), new ClassificationResult("B", 0.5)), 1),
            },
            priors: new[]
            {
                new PriorLane(new FixedPriorSource(new TextEvidence("A", Math.Log(2))), 1),
            });

        var results = blend.Predict("anything", limit: 3);

        Assert.Equal(new[] { "A", "B" }, results.Select(result => result.Category));
        Assert.Equal(2.0 / 3.0, results[0].Probability, precision: 10);
        Assert.Equal(1.0 / 3.0, results[1].Probability, precision: 10);
    }

    [Fact]
    public void APriorOnACandidateCategoryIsIgnored()
    {
        var withPrior = new BlendTextClassifier(
            new[]
            {
                new TextClassifierLane(new FixedClassifier(
                    new ClassificationResult("A", 0.6), new ClassificationResult("B", 0.4)), 1),
            },
            new[]
            {
                new CandidateLane(new FixedCandidateSource(new TextEvidence("X", 1)), 1),
            },
            new[]
            {
                new PriorLane(new FixedPriorSource(new TextEvidence("X", 10)), 1),
            });

        var withoutPrior = new BlendTextClassifier(
            new[]
            {
                new TextClassifierLane(new FixedClassifier(
                    new ClassificationResult("A", 0.6), new ClassificationResult("B", 0.4)), 1),
            },
            new[]
            {
                new CandidateLane(new FixedCandidateSource(new TextEvidence("X", 1)), 1),
            });

        Assert.Equal(
            withoutPrior.Predict("anything", limit: 10).Select(result => (result.Category, result.Probability)),
            withPrior.Predict("anything", limit: 10).Select(result => (result.Category, result.Probability)));
    }

    [Fact]
    public void APriorOnACategoryNoLaneProducedIsIgnored()
    {
        var blend = new BlendTextClassifier(
            new[]
            {
                new TextClassifierLane(new FixedClassifier(
                    new ClassificationResult("A", 0.5), new ClassificationResult("B", 0.5)), 1),
            },
            priors: new[]
            {
                new PriorLane(new FixedPriorSource(new TextEvidence("Z", 10)), 1),
            });

        var results = blend.Predict("anything", limit: 3);

        Assert.DoesNotContain(results, result => result.Category == "Z");
        Assert.Equal(0.5, results[0].Probability, precision: 10);
    }

    [Fact]
    public void ExclusionsApplyToCandidatesAndPriorsAndRenormalize()
    {
        var blend = new BlendTextClassifier(
            new[]
            {
                new TextClassifierLane(new FixedClassifier(
                    new ClassificationResult("A", 0.5), new ClassificationResult("B", 0.5)), 1),
            },
            new[]
            {
                new CandidateLane(new FixedCandidateSource(new TextEvidence("X", 1)), 1),
            },
            new[]
            {
                new PriorLane(new FixedPriorSource(new TextEvidence("A", 10)), 1),
            });

        var results = blend.Predict("anything", limit: 10, new HashSet<string>(new[] { "A", "X" }));

        Assert.Equal(new[] { "B" }, results.Select(result => result.Category));
        Assert.Equal(1.0, results[0].Probability, precision: 10);
    }

    [Fact]
    public void TrainTrainsEveryLaneOnTheSameMaterializedCorpus()
    {
        var first = new CountingClassifier();
        var second = new CountingClassifier();
        var blend = new BlendTextClassifier(new[]
        {
            new TextClassifierLane(first, 0.5),
            new TextClassifierLane(second, 0.5),
        });

        blend.Train(OnePass());

        Assert.Equal(1, first.TrainCalls);
        Assert.Equal(1, second.TrainCalls);
        Assert.Equal(Corpus, first.Trained);
        Assert.Equal(Corpus, second.Trained);

        static IEnumerable<SearchDocument> OnePass()
        {
            foreach (SearchDocument document in Corpus)
                yield return document;
        }
    }

    [Fact]
    public void AnEmptyLaneListIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new BlendTextClassifier(Array.Empty<TextClassifierLane>()));
    }

    [Fact]
    public void ANegativeLaneWeightIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new BlendTextClassifier(new[]
        {
            new TextClassifierLane(new FixedClassifier(new ClassificationResult("A", 1)), -0.1),
        }));
    }

    [Fact]
    public void ANegativeCandidateScoreIsRejected()
    {
        var blend = new BlendTextClassifier(
            new[] { new TextClassifierLane(new FixedClassifier(new ClassificationResult("A", 1)), 1) },
            new[] { new CandidateLane(new FixedCandidateSource(new TextEvidence("X", -1)), 1) });

        Assert.Throws<ArgumentException>(() => blend.Predict("anything"));
    }

    [Fact]
    public void TheFusedClassifierIsTheTwoLaneBlend()
    {
        var random = new Random(7); // NOSONAR:S2245 (fixed seed: the comparison is reproducible)
        string[] categories = { "A", "B", "C", "D", "E" };

        for (int trial = 0; trial < 200; trial++)
        {
            ClassificationResult[] first = RandomDistribution(random, categories);
            ClassificationResult[] second = RandomDistribution(random, categories);
            double weight = random.NextDouble();
            int limit = random.Next(1, 6);

            var fused = new FusedTextClassifier(
                new FixedClassifier(first), new FixedClassifier(second), weight);
            var blend = new BlendTextClassifier(new[]
            {
                new TextClassifierLane(new FixedClassifier(first), 1 - weight),
                new TextClassifierLane(new FixedClassifier(second), weight),
            });

            var expected = fused.Predict("anything", limit);
            var actual = blend.Predict("anything", limit);

            Assert.Equal(expected.Count, actual.Count);

            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Category, actual[i].Category);
                Assert.Equal(expected[i].Probability, actual[i].Probability, precision: 12);
            }
        }
    }

    private static ClassificationResult[] RandomDistribution(Random random, string[] categories)
    {
        var results = new List<ClassificationResult>();

        foreach (string category in categories)
        {
            if (random.Next(4) != 0)
                results.Add(new ClassificationResult(category, random.NextDouble() + 1e-6));
        }

        return results.ToArray();
    }

    private sealed class FixedClassifier(params ClassificationResult[] results) : ITextClassifier
    {
        public void Train(IEnumerable<SearchDocument> documents) { }

        public IReadOnlyList<ClassificationResult> Predict(
            string text, int limit = 3, IReadOnlySet<string>? excludedCategories = null) =>
            results.Take(limit).ToArray();

        public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) =>
            results.Length > 0 ? results[0].Category : null;
    }

    private sealed class IncrementalClassifier : ITextClassifier, IIncrementalTextClassifier
    {
        public List<SearchDocument> Learned { get; } = new();
        public List<SearchDocument> Unlearned { get; } = new();

        public void Train(IEnumerable<SearchDocument> documents) { }

        public void Learn(SearchDocument document) => Learned.Add(document);

        public void Unlearn(SearchDocument document) => Unlearned.Add(document);

        public IReadOnlyList<ClassificationResult> Predict(
            string text, int limit = 3, IReadOnlySet<string>? excludedCategories = null) =>
            new[] { new ClassificationResult("A", 1.0) };

        public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) => "A";
    }

    private sealed class CountingClassifier : ITextClassifier
    {
        public int TrainCalls { get; private set; }

        public IReadOnlyList<SearchDocument> Trained { get; private set; } = Array.Empty<SearchDocument>();

        public void Train(IEnumerable<SearchDocument> documents)
        {
            TrainCalls++;
            Trained = documents.ToArray();
        }

        public IReadOnlyList<ClassificationResult> Predict(
            string text, int limit = 3, IReadOnlySet<string>? excludedCategories = null) =>
            new[] { new ClassificationResult("A", 1.0) };

        public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) => "A";
    }

    private sealed class FixedCandidateSource(params TextEvidence[] evidence) : ICandidateTextSource
    {
        public IReadOnlyList<TextEvidence> Candidates(string text) => evidence;
    }

    private sealed class FixedPriorSource(params TextEvidence[] evidence) : ITextPriorSource
    {
        public IReadOnlyList<TextEvidence> Prior(string text) => evidence;
    }
}
