using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Incremental training-data addition: <see cref="NaiveBayesClassifier.Learn"/> must be
/// indistinguishable from having handed the whole corpus to <c>Train</c> at once, and
/// <c>Unlearn</c> must be the exact inverse. The property that matters is parity, so most of
/// these compare a learned model against a trained one rather than asserting a number.
/// </summary>
public class IncrementalTextClassifierTests
{
    private static readonly SearchDocument[] Corpus =
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

    private static readonly string[] Queries =
    {
        "forgotten password",
        "monthly invoice statement",
        "delivery delay tracking order",
        "connection website",
        "zzz nothing matches",
        "internet invoice order password statement",
    };

    private static void AssertSameModel(NaiveBayesClassifier expected, NaiveBayesClassifier actual)
    {
        foreach (string query in Queries)
        {
            var expectedResults = expected.Predict(query, limit: 10);
            var actualResults = actual.Predict(query, limit: 10);

            Assert.Equal(expectedResults.Count, actualResults.Count);
            for (int i = 0; i < expectedResults.Count; i++)
            {
                Assert.Equal(expectedResults[i].Category, actualResults[i].Category);
                Assert.Equal(expectedResults[i].Probability, actualResults[i].Probability, precision: 10);
            }
        }
    }

    [Fact]
    public void TheClassifier_ExposesTheIncrementalCapability()
    {
        ITextClassifier classifier = new NaiveBayesClassifier();

        Assert.IsAssignableFrom<IIncrementalTextClassifier>(classifier);
    }

    // The headline property. Every option that reads a different part of the model state is in the
    // theory below, because parity that only holds on the defaults is not parity.
    [Fact]
    public void Learn_OneDocumentAtATime_EqualsTrainingTheWholeCorpusAtOnce()
    {
        var learned = new NaiveBayesClassifier();
        foreach (var document in Corpus)
            learned.Learn(document);

        var trained = new NaiveBayesClassifier();
        trained.Train(Corpus);

        AssertSameModel(trained, learned);
    }

    /// <summary>
    /// The option combinations both theories below run over, each under a name.
    /// </summary>
    /// <remarks>
    /// The name is what the theory passes, not the options object. <c>NaiveBayesOptions</c> is a
    /// public record of primitives with no serialization contract, so a row carrying one is opaque
    /// to Test Explorer: the run shows twelve passing tests named <c>(options)</c> instead of twelve
    /// naming the combination that failed. A <c>string</c> row serializes, and carries more
    /// information than the object did.
    /// </remarks>
    private static readonly (string Name, NaiveBayesOptions Options)[] Combinations =
    [
        ("defaults", new NaiveBayesOptions()),
        ("idf=document-count", new NaiveBayesOptions { IdfMode = IdfMode.DocumentCount }),
        ("idf=class-count", new NaiveBayesOptions { IdfMode = IdfMode.ClassCount }),
        ("alpha=0.25", new NaiveBayesOptions { Alpha = 0.25 }),
        ("alpha=50", new NaiveBayesOptions { Alpha = 50.0 }),
        ("smooth-priors", new NaiveBayesOptions { SmoothPriors = true }),
        ("skip-oov", new NaiveBayesOptions { SkipOutOfVocabularyTokens = true }),
        ("temperature=0.5", new NaiveBayesOptions { Temperature = 0.5 }),
        ("temperature=4", new NaiveBayesOptions { Temperature = 4.0 }),
        ("complement", new NaiveBayesOptions { Complement = true }),
        ("complement,idf=document-count", new NaiveBayesOptions { Complement = true, IdfMode = IdfMode.DocumentCount }),
        ("every-option-together", new NaiveBayesOptions
        {
            Complement = true,
            IdfMode = IdfMode.ClassCount,
            Alpha = 2.0,
            SmoothPriors = true,
            SkipOutOfVocabularyTokens = true,
            Temperature = 0.75,
        }),
    ];

    public static TheoryData<string> OptionCombinations()
    {
        var data = new TheoryData<string>();

        foreach (var (name, _) in Combinations)
            data.Add(name);

        return data;
    }

    /// <summary>The options behind a row name. Throws rather than defaulting, so a typo is loud.</summary>
    private static NaiveBayesOptions OptionsFor(string name) =>
        Array.Find(Combinations, combination => combination.Name == name).Options;

    [Theory]
    [MemberData(nameof(OptionCombinations))]
    public void Learn_MatchesBatchTrain_UnderEveryOptionCombination(string combination)
    {
        NaiveBayesOptions options = OptionsFor(combination);

        var learned = new NaiveBayesClassifier(options: options);
        foreach (var document in Corpus)
            learned.Learn(document);

        var trained = new NaiveBayesClassifier(options: options);
        trained.Train(Corpus);

        AssertSameModel(trained, learned);
    }

    [Fact]
    public void Learn_Repeats_AccumulateLikeRepeatedDocumentsInTheCorpus()
    {
        // A document learned twice must weigh exactly as much as two copies handed to Train, or
        // "learn the same corrected example again" silently means something weaker.
        SearchDocument[] doubled = Corpus.Concat(Corpus).ToArray();

        var learned = new NaiveBayesClassifier();
        foreach (var document in Corpus)
        {
            learned.Learn(document);
            learned.Learn(document);
        }

        var trained = new NaiveBayesClassifier();
        trained.Train(doubled);

        AssertSameModel(trained, learned);
    }

    [Fact]
    public void Learn_IgnoresDocumentsWithoutCategory_JustAsTrainDoes()
    {
        SearchDocument[] withUncategorised = Corpus
            .Concat(new[] { new SearchDocument("x", "uncategorised noise words") })
            .ToArray();

        var learned = new NaiveBayesClassifier();
        foreach (var document in withUncategorised)
            learned.Learn(document);

        var trained = new NaiveBayesClassifier();
        trained.Train(withUncategorised);

        AssertSameModel(trained, learned);
    }

    [Fact]
    public void Learn_OnAnEmptyDocument_AddsTheCategoryWithNoVocabulary()
    {
        var learned = new NaiveBayesClassifier();
        learned.Learn(new SearchDocument("1", "", Category: "Empty"));
        learned.Learn(new SearchDocument("2", "real tokens here", Category: "Real"));

        var trained = new NaiveBayesClassifier();
        trained.Train(new[]
        {
            new SearchDocument("1", "", Category: "Empty"),
            new SearchDocument("2", "real tokens here", Category: "Real"),
        });

        AssertSameModel(trained, learned);
    }

    [Fact]
    public void Learn_AfterTrain_AccumulatesOntoTheCorpus()
    {
        var learned = new NaiveBayesClassifier();
        learned.Train(Corpus);
        learned.Learn(new SearchDocument("9", "package delivery tracking number", Category: "Shipping"));

        var trained = new NaiveBayesClassifier();
        trained.Train(Corpus.Append(new SearchDocument("9", "package delivery tracking number", Category: "Shipping")));

        AssertSameModel(trained, learned);
    }

    [Fact]
    public void Unlearn_IsTheExactInverseOfLearn()
    {
        var classifier = new NaiveBayesClassifier();
        classifier.Train(Corpus);
        var addition = new SearchDocument("9", "package delivery tracking number", Category: "Shipping");

        var before = classifier.Predict("delivery tracking", limit: 10).Select(r => (r.Category, r.Probability));

        classifier.Learn(addition);
        classifier.Unlearn(addition);

        Assert.Equal(before, classifier.Predict("delivery tracking", limit: 10).Select(r => (r.Category, r.Probability)));
    }

    // The strongest form of the whole contract: after learning a corpus and then retracting part of
    // it, the model must be indistinguishable from a fresh classifier trained on what is left.
    // This is what pins the document-frequency and vocabulary bookkeeping, which a per-category
    // term count alone would get wrong.
    [Theory]
    [MemberData(nameof(OptionCombinations))]
    public void LearnThenPartiallyUnlearn_EqualsTrainingOnWhatIsLeft(string combination)
    {
        NaiveBayesOptions options = OptionsFor(combination);

        var retracted = new HashSet<string> { "3", "6" };

        var learned = new NaiveBayesClassifier(options: options);
        foreach (var document in Corpus)
            learned.Learn(document);

        foreach (var document in Corpus.Where(d => retracted.Contains(d.Id)))
            learned.Unlearn(document);

        var trained = new NaiveBayesClassifier(options: options);
        trained.Train(Corpus.Where(d => !retracted.Contains(d.Id)));

        AssertSameModel(trained, learned);
    }

    [Fact]
    public void LearnThenUnlearnEverything_EqualsAFreshlyConstructedClassifier()
    {
        var learned = new NaiveBayesClassifier();
        foreach (var document in Corpus)
            learned.Learn(document);
        foreach (var document in Corpus)
            learned.Unlearn(document);

        var fresh = new NaiveBayesClassifier();

        Assert.Empty(learned.Predict("forgotten password", limit: 10));
        Assert.Null(learned.PredictBest("forgotten password"));
        Assert.Equal(fresh.Predict("forgotten password", limit: 10).Count,
                     learned.Predict("forgotten password", limit: 10).Count);
    }

    // "Unlearning everything" has to leave nothing that a query could still see: no terms, no
    // categories, no prior. Only an empty result proves the vocabulary went too.
    [Fact]
    public void Unlearn_RemovesTermsFromTheVocabularyToo()
    {
        var options = new NaiveBayesOptions { IdfMode = IdfMode.ClassCount };
        var classifier = new NaiveBayesClassifier(options: options);
        classifier.Train(Corpus);
        classifier.Unlearn(Corpus[0]);   // the only "internet" and "connection" document
        classifier.Unlearn(Corpus[1]);

        // "internet" existed only in the two removed documents, so it is now a corpus-unknown term.
        // Under ClassCount that must leave every class scoring on its prior alone — three classes
        // remain, all finite, none NaN. The term is gone from the vocabulary, which is the point:
        // had it survived, its idf lookup would still resolve instead of being an out-of-vocabulary
        // term weighted at zero.
        var results = classifier.Predict("internet", limit: 10);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.False(double.IsNaN(r.Probability)));
        Assert.Equal(1.0, results.Sum(r => r.Probability), precision: 10);

        // And the surviving vocabulary still discriminates: a term that is still there, and still
        // belongs to Shipping alone, must be unaffected by the two retractions.
        Assert.Equal("Shipping", classifier.PredictBest("delivery delay"));
    }

    [Fact]
    public void Unlearn_RetiresACategoryOnceItsLastDocumentIsGone()
    {
        var classifier = new NaiveBayesClassifier();
        classifier.Train(Corpus);
        classifier.Unlearn(Corpus[2]);   // last Support document
        classifier.Unlearn(Corpus[0]);
        classifier.Unlearn(Corpus[1]);

        var results = classifier.Predict("forgotten password", limit: 10);

        Assert.DoesNotContain(results, r => r.Category == "Support");
        Assert.Equal("Billing", classifier.PredictBest("monthly invoice"));
    }

    [Fact]
    public void Unlearn_UnknownCategory_IsANoOp()
    {
        var classifier = new NaiveBayesClassifier();
        classifier.Train(Corpus);
        var before = classifier.Predict("invoice", limit: 10).Select(r => (r.Category, r.Probability));

        classifier.Unlearn(new SearchDocument("x", "never learned", Category: "Ghost"));

        Assert.Equal(before, classifier.Predict("invoice", limit: 10).Select(r => (r.Category, r.Probability)));
    }

    [Fact]
    public void Unlearn_WithoutACategory_IsANoOp()
    {
        var classifier = new NaiveBayesClassifier();
        classifier.Train(Corpus);
        var before = classifier.Predict("invoice", limit: 10).Select(r => (r.Category, r.Probability));

        classifier.Unlearn(new SearchDocument("x", "some uncategorised text"));
        classifier.Unlearn(new SearchDocument("x", "some text", Category: string.Empty));

        Assert.Equal(before, classifier.Predict("invoice", limit: 10).Select(r => (r.Category, r.Probability)));
    }

    // Unlearn is arithmetic, not bookkeeping: it always removes one document from the class and
    // subtracts that text's contribution, and it does not check whether the text was ever learned.
    // So unlearning something the model never saw costs the class one document of prior. That is
    // the documented contract, pinned here so a future change to it is deliberate — the alternative
    // would be silently returning "no-op" for a call that did change the model.
    [Fact]
    public void Unlearn_OfSomethingNeverLearned_StillRemovesOneDocumentFromTheClass()
    {
        var classifier = new NaiveBayesClassifier();
        classifier.Train(new[]
        {
            new SearchDocument("1", "chat", Category: "A"),
            new SearchDocument("2", "chat", Category: "B"),
            new SearchDocument("3", "mail", Category: "B"),
        });

        var before = classifier.Predict("chat", limit: 5).Select(r => r.Category).ToArray();
        Assert.Equal(new[] { "B", "A" }, before);

        // "zebra" was never learned and belongs to no class, so the class-conditional part of the
        // score cannot move — but the class loses a document of prior, and B is no longer ahead.
        classifier.Unlearn(new SearchDocument("x", "zebra", Category: "B"));

        var after = classifier.Predict("chat", limit: 5).Select(r => r.Category).ToArray();

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Train_ResetsWhatWasLearned()
    {
        var classifier = new NaiveBayesClassifier();
        classifier.Train(Corpus);
        classifier.Train(new[] { new SearchDocument("z", "alpha beta", Category: "Only") });

        Assert.Equal("Only", classifier.PredictBest("beta"));
        Assert.DoesNotContain(classifier.Predict("alpha", limit: 10), r => r.Category == "Support");
    }

    // Learn/Unlearn and the reinforcement ledger are two different things and must not interfere:
    // one is corpus state, the other is user feedback laid over it.
    [Fact]
    public void Learn_DoesNotDisturbReinforcement()
    {
        var classifier = new NaiveBayesClassifier();
        classifier.Train(Corpus);
        classifier.Reinforce("invoice", "Support", weight: 4.0);

        classifier.Learn(new SearchDocument("9", "package delivery tracking", Category: "Shipping"));

        Assert.Equal("Support", classifier.PredictBest("invoice"));
    }

    [Fact]
    public void Unlearn_DoesNotDisturbReinforcement()
    {
        var classifier = new NaiveBayesClassifier();
        classifier.Train(Corpus);
        classifier.Reinforce("invoice", "Support", weight: 4.0);

        classifier.Unlearn(Corpus[3]);   // a Billing document, learned and then retracted

        Assert.Equal("Support", classifier.PredictBest("invoice"));
    }

    [Fact]
    public void ForgetReinforcement_DoesNotUndoLearning()
    {
        var classifier = new NaiveBayesClassifier();
        classifier.Train(Corpus);
        classifier.Learn(new SearchDocument("9", "package delivery tracking", Category: "Shipping"));
        classifier.Reinforce("invoice", "Support", weight: 4.0);

        classifier.ForgetReinforcement();

        var trained = new NaiveBayesClassifier();
        trained.Train(Corpus.Append(new SearchDocument("9", "package delivery tracking", Category: "Shipping")));

        AssertSameModel(trained, classifier);
    }

    [Fact]
    public void Learn_RejectsNullDocument()
    {
        var classifier = new NaiveBayesClassifier();

        Assert.Throws<ArgumentNullException>(() => classifier.Learn(null!));
        Assert.Throws<ArgumentNullException>(() => classifier.Unlearn(null!));
    }
}
