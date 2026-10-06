using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class NaiveBayesAbstentionTests
{
    // Unbalanced on purpose: 7 documents against 3, so the class prior reads 0.70 — the figure
    // the abstention exists to stop reporting as an answer.
    private static SearchDocument[] Training()
    {
        var docs = new List<SearchDocument>();

        for (int i = 0; i < 7; i++)
            docs.Add(new SearchDocument($"bug{i}", $"crash segfault heap overflow exception trace {i}", Category: "defect"));

        for (int i = 0; i < 3; i++)
            docs.Add(new SearchDocument($"feat{i}", $"feature api endpoint route handler {i}", Category: "feature"));

        return docs.ToArray();
    }

    private static NaiveBayesClassifier Trained(NaiveBayesOptions? options = null)
    {
        var classifier = new NaiveBayesClassifier(options: options);
        classifier.Train(Training());
        return classifier;
    }

    private const string French = "le client souhaite une facture mensuelle";
    private const string Nonsense = "zzz qqq www iii";

    [Fact]
    public void HasAnyVocabularyOverlap_TrueWhenASharedTokenExists()
    {
        var classifier = Trained();

        Assert.True(classifier.HasAnyVocabularyOverlap("a segfault in the heap"));
        Assert.True(classifier.HasAnyVocabularyOverlap("the api endpoint"));
    }

    [Fact]
    public void HasAnyVocabularyOverlap_FalseOnTextTheModelHasNeverSeen()
    {
        // The condition the abstention exists for: zero shared tokens, and the score the classic
        // path reports for it is above the prior rather than at it — 0.97 for the French prose,
        // 0.88 for nonsense, measured on this training set.
        var classifier = Trained();

        Assert.False(classifier.HasAnyVocabularyOverlap(French));
        Assert.False(classifier.HasAnyVocabularyOverlap(Nonsense));
    }

    [Fact]
    public void HasAnyVocabularyOverlap_FalseOnTextThatTokenizesToNothing()
    {
        var classifier = Trained();

        Assert.False(classifier.HasAnyVocabularyOverlap(string.Empty));
        Assert.False(classifier.HasAnyVocabularyOverlap("   "));
        Assert.False(classifier.HasAnyVocabularyOverlap("!?.,-"));
    }

    [Fact]
    public void HasAnyVocabularyOverlap_FalseOnAnUntrainedModel()
    {
        var classifier = new NaiveBayesClassifier();
        classifier.Train(Array.Empty<SearchDocument>());

        Assert.False(classifier.HasAnyVocabularyOverlap("anything at all"));
    }

    [Fact]
    public void HasAnyVocabularyOverlap_FalseAfterTheOnlyEvidenceIsUnlearned()
    {
        var document = new SearchDocument("1", "a distinctive zephyr", Category: "x");
        var classifier = new NaiveBayesClassifier();
        classifier.Train(new[] { document });

        Assert.True(classifier.HasAnyVocabularyOverlap("a distinctive zephyr"));

        classifier.Unlearn(document);

        Assert.False(classifier.HasAnyVocabularyOverlap("a distinctive zephyr"));
    }

    [Fact]
    public void HasAnyVocabularyOverlap_ConsidersReinforcedTerms()
    {
        // Reinforce writes to a ledger, not to the corpus counts, and a term the corpus never
        // saw still contributes its evidence at full weight there. Abstaining on such an input
        // would discard the feedback the user gave explicitly.
        var classifier = new NaiveBayesClassifier();
        classifier.Train(new[]
        {
            new SearchDocument("1", "a name the corpus knows", Category: "infra"),
            new SearchDocument("2", "another known name", Category: "infra"),
            new SearchDocument("3", "separate known name", Category: "other"),
        });

        Assert.False(classifier.HasAnyVocabularyOverlap("cloud migration"));

        classifier.Reinforce("cloud migration", "infra");

        Assert.True(classifier.HasAnyVocabularyOverlap("cloud migration"));
        Assert.True(classifier.HasAnyVocabularyOverlap("an unknown cloud migration"));
    }

    [Fact]
    public void HasAnyVocabularyOverlap_OverWeightedTokens_IgnoresWeight()
    {
        // The question is whether the model has seen the word, not how much the caller trusts it.
        var classifier = Trained();

        Assert.True(classifier.HasAnyVocabularyOverlap(new[]
        {
            new WeightedToken("segfault", 0.01),
            new WeightedToken("heap", 0.0),
        }));

        Assert.False(classifier.HasAnyVocabularyOverlap(new[]
        {
            new WeightedToken("cloud", 1.0),
            new WeightedToken("migration", 1.0),
        }));
    }

    [Fact]
    public void AbstainWithoutVocabularyOverlap_ReturnsNothingOnUnseenText()
    {
        var classifier = Trained(options: new NaiveBayesOptions { AbstainWithoutVocabularyOverlap = true });

        Assert.Empty(classifier.Predict(French));
        Assert.Empty(classifier.Predict(Nonsense));
        Assert.Empty(classifier.Predict("   "));
    }

    [Fact]
    public void AbstainWithoutVocabularyOverlap_LeavesARealPredictionAlone()
    {
        var classifier = Trained(options: new NaiveBayesOptions { AbstainWithoutVocabularyOverlap = true });

        var results = classifier.Predict("crash heap overflow");

        Assert.NotEmpty(results);
        Assert.Equal("defect", results[0].Category);
    }

    [Fact]
    public void AbstainWithoutVocabularyOverlap_LeavesReinforcedTextClassifiable()
    {
        var classifier = Trained(options: new NaiveBayesOptions { AbstainWithoutVocabularyOverlap = true });
        classifier.Reinforce("cloud migration", "feature");

        Assert.NotEmpty(classifier.Predict("cloud migration"));
    }

    [Fact]
    public void AbstainWithoutVocabularyOverlap_BestIsNullWhenThereIsNothingToReport()
    {
        var classifier = Trained(options: new NaiveBayesOptions { AbstainWithoutVocabularyOverlap = true });

        Assert.Null(classifier.PredictBest(French));
        Assert.Equal("defect", classifier.PredictBest("crash heap overflow"));
    }

    [Fact]
    public void AbstainWithoutVocabularyOverlap_AppliesToTheWeightedTokenOverloadToo()
    {
        var classifier = Trained(options: new NaiveBayesOptions { AbstainWithoutVocabularyOverlap = true });

        var unseen = classifier.Predict(new[] { WeightedToken.Full("cloud"), WeightedToken.Full("migration") });
        Assert.Empty(unseen);

        var seen = classifier.Predict(new[] { WeightedToken.Full("segfault") });
        Assert.NotEmpty(seen);
    }

    [Fact]
    public void AbstainWithoutVocabularyOverlap_DefaultsOff_LeavingExistingScoresIntact()
    {
        // The option is opt-in so no existing caller's numbers move. This asserts the behaviour it
        // does *not* change: the classic path still answers, and answers above the prior.
        var classifier = Trained();

        var french = classifier.Predict(French);

        Assert.NotEmpty(french);
        Assert.True(french[0].Probability > 0.70,
            $"expected the smoothing artifact the option declines to report, got {french[0].Probability:F2}");
    }

    [Fact]
    public void AbstainWithoutVocabularyOverlap_DoesNotChangeAnInVocabularyPrediction()
    {
        var with = Trained(options: new NaiveBayesOptions { AbstainWithoutVocabularyOverlap = true });
        var without = Trained();

        var text = "crash heap overflow";

        Assert.Equal(
            without.Predict(text).Select(r => (r.Category, r.Probability)),
            with.Predict(text).Select(r => (r.Category, r.Probability)));
    }
}
