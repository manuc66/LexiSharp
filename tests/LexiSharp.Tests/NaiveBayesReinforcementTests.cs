using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The reinforcement ledger: user feedback that nudges a trained model without a retrain.
/// Every test here is about the ledger's own contract, so none of them may depend on the shape of
/// the base Naive Bayes scores beyond what they compare against.
/// </summary>
public class NaiveBayesReinforcementTests
{
    private static readonly SearchDocument[] Corpus =
    {
        new("1", "internet connection problem", Category: "Support"),
        new("2", "cannot access the website", Category: "Support"),
        new("3", "forgotten password", Category: "Support"),
        new("4", "monthly consumption invoice", Category: "Billing"),
        new("5", "online invoice statement", Category: "Billing"),
        new("6", "card payment problem", Category: "Billing"),
    };

    private static NaiveBayesClassifier Trained(NaiveBayesOptions? options = null)
    {
        var classifier = new NaiveBayesClassifier(options: options);
        classifier.Train(Corpus);
        return classifier;
    }

    private static (string Category, double Probability)[] Snapshot(NaiveBayesClassifier classifier, string query) =>
        classifier.Predict(query, limit: 10).Select(r => (r.Category, r.Probability)).ToArray();

    [Fact]
    public void TheClassifier_ExposesTheReinforcementCapability()
    {
        // A caller that only holds an ITextClassifier can still detect and use the capability
        // without naming the concrete type.
        ITextClassifier classifier = Trained();

        Assert.IsAssignableFrom<IReinforceableTextClassifier>(classifier);
    }

    // The motivating case: a user picks a search result and says "this text is a Support request",
    // and the model has to agree on the very next call.
    [Fact]
    public void Reinforce_EnoughOfIt_MovesTheDecisionToTheReinforcedCategory()
    {
        var classifier = Trained();

        Assert.Equal("Billing", classifier.PredictBest("monthly invoice"));

        classifier.Reinforce("monthly invoice", "Support", weight: 3.0);

        Assert.Equal("Support", classifier.PredictBest("monthly invoice"));
    }

    [Fact]
    public void Unreinforce_IsTheExactInverseOfReinforce()
    {
        var classifier = Trained();
        var before = Snapshot(classifier, "invoice payment");

        classifier.Reinforce("invoice payment", "Support", weight: 2.5);
        classifier.Unreinforce("invoice payment", "Support", weight: 2.5);

        Assert.Equal(before, Snapshot(classifier, "invoice payment"));
    }

    [Fact]
    public void Unreinforce_NegatesTheReinforcement()
    {
        // The ledger is additive, so "un-reinforce" and "reinforce the other way" are the same
        // operation. Pinned deliberately: it is the property callers rely on to build a toggle.
        var classifier = Trained();

        classifier.Reinforce("invoice payment", "Support", weight: 2.5);
        classifier.Reinforce("invoice payment", "Support", weight: -5.0);

        var negated = Snapshot(classifier, "invoice payment");

        var other = Trained();
        other.Reinforce("invoice payment", "Support", weight: -2.5);

        Assert.Equal(other.Predict("invoice payment", limit: 10).Select(r => (r.Category, r.Probability)),
                     negated);
    }

    [Fact]
    public void ForgetReinforcement_RestoresTheTrainedModel()
    {
        var classifier = Trained();
        var before = Snapshot(classifier, "invoice payment");

        classifier.Reinforce("invoice payment", "Support", weight: 4.0);
        Assert.NotEqual(before, Snapshot(classifier, "invoice payment"));

        classifier.ForgetReinforcement();

        Assert.Equal(before, Snapshot(classifier, "invoice payment"));
    }

    // A forgotten ledger has to leave the model indistinguishable from one that was never
    // reinforced — including the category set, the priors and the vocabulary.
    [Fact]
    public void ForgetReinforcement_LeavesTheModelIdenticalToAFreshlyTrainedOne()
    {
        var churned = Trained();
        churned.Reinforce("invoice payment", "Support", weight: 4.0);
        churned.Reinforce("brand new vocabulary", "Billing", weight: -1.0);
        churned.Unreinforce("internet connection problem", "Billing", weight: 2.0);
        churned.ForgetReinforcement();

        var fresh = Trained();

        foreach (string query in new[] { "invoice payment", "internet", "nothing matches at all" })
        {
            var expected = fresh.Predict(query, limit: 10).Select(r => (r.Category, r.Probability));
            var actual = churned.Predict(query, limit: 10).Select(r => (r.Category, r.Probability));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Reinforce_AgainstACategoryHandsTheDecisionToItsCompetitor()
    {
        // The negative case the ledger exists for: "this search is not a billing question", said
        // about a text the model currently scores as billing. Support never sees the word at all,
        // and still wins.
        var classifier = Trained();

        Assert.Equal("Billing", classifier.PredictBest("monthly invoice"));

        classifier.Reinforce("monthly invoice", "Billing", weight: -1.0);

        Assert.Equal("Support", classifier.PredictBest("monthly invoice"));
    }

    [Fact]
    public void Reinforce_Negative_StaysValidWhenItOutweighsEverythingElse()
    {
        var classifier = Trained();

        classifier.Reinforce("monthly invoice", "Billing", weight: -50.0);

        var results = classifier.Predict("monthly invoice", limit: 10);

        Assert.Equal("Support", results[0].Category);
        Assert.All(results, r => Assert.False(double.IsNaN(r.Probability) || double.IsInfinity(r.Probability)));
        Assert.Equal(1.0, results.Sum(r => r.Probability), precision: 10);
    }

    [Fact]
    public void Reinforce_EffectGrowsMonotonicallyWithTheWeight()
    {
        var weights = new[] { 0.25, 0.5, 1.0, 2.0, 4.0 };
        var probabilities = new List<double>();

        foreach (double weight in weights)
        {
            var classifier = Trained();
            classifier.Reinforce("invoice payment", "Support", weight: weight);
            probabilities.Add(classifier.Predict("invoice payment", limit: 3)
                .First(r => r.Category == "Support").Probability);
        }

        for (int i = 1; i < probabilities.Count; i++)
            Assert.True(probabilities[i] > probabilities[i - 1],
                $"weight {weights[i]} should weigh more than {weights[i - 1]}.");
    }

    // Under ClassCount the model deliberately zeroes any term the corpus never saw, which is right
    // for a likelihood and wrong for an explicit user correction: "this text means Support" has to
    // say something even when every word in it is new to the corpus.
    [Theory]
    [InlineData(IdfMode.None)]
    [InlineData(IdfMode.DocumentCount)]
    [InlineData(IdfMode.ClassCount)]
    public void Reinforce_TextWhoseEveryTermIsUnknownToTheCorpus_StillCounts(IdfMode idfMode)
    {
        var classifier = Trained(options: new NaiveBayesOptions { IdfMode = idfMode });

        classifier.Reinforce("kubernetes terraform", "Support", weight: 1.0);

        var results = classifier.Predict("kubernetes terraform", limit: 3);

        Assert.Equal("Support", results[0].Category);
        Assert.True(results[0].Probability > 0.5, $"expected a clear Support win, got {results[0].Probability}.");
    }

    // A correction should not be inflated by the user repeating a word in the search box: the
    // ledger counts distinct terms, the way a class-conditional vocabulary is counted.
    [Fact]
    public void Reinforce_CountsADistinctTermOnce_NoMatterHowOftenTheUserRepeatsIt()
    {
        var once = Trained();
        once.Reinforce("invoice", "Support", weight: 1.0);

        var repeated = Trained();
        repeated.Reinforce("invoice invoice invoice", "Support", weight: 1.0);

        Assert.Equal(once.Predict("invoice", limit: 3).Select(r => (r.Category, r.Probability)),
                     repeated.Predict("invoice", limit: 3).Select(r => (r.Category, r.Probability)));
    }

    [Fact]
    public void Reinforce_AccumulatesAcrossCalls()
    {
        var accumulated = Trained();
        accumulated.Reinforce("invoice", "Support", weight: 0.75);
        accumulated.Reinforce("invoice", "Support", weight: 0.75);

        var single = Trained();
        single.Reinforce("invoice", "Support", weight: 1.5);

        Assert.Equal(single.Predict("invoice", limit: 3).Select(r => (r.Category, r.Probability)),
                     accumulated.Predict("invoice", limit: 3).Select(r => (r.Category, r.Probability)));
    }

    // The ledger records raw mass; the query's own token weight scales how much of it a given
    // prediction gets to see. (The scaling is exact in the log score, not in the softmax
    // probability, so this pins the two things that are exact: a weaker query term moves the
    // posterior strictly less, and a zero-weighted term moves it not at all.)
    [Fact]
    public void Reinforce_ScalesWithTheQueryTokenWeight()
    {
        static double SupportProbability(NaiveBayesClassifier classifier, WeightedToken invoice) =>
            classifier.Predict(new[] { invoice }, limit: 3).First(r => r.Category == "Support").Probability;

        var reinforced = Trained();
        reinforced.Reinforce("invoice", "Support", weight: 2.0);

        double baseline = SupportProbability(Trained(), WeightedToken.Full("invoice"));
        double full = SupportProbability(reinforced, WeightedToken.Full("invoice"));
        double quarter = SupportProbability(reinforced, new WeightedToken("invoice", 0.25));

        Assert.True(full > baseline, "A full-weight query term should see the whole ledger entry.");
        Assert.True(quarter > baseline, "A quarter-weighted query term should still register.");
        Assert.True(quarter < full, "Weaker query weight should mean a weaker effect.");
    }

    [Fact]
    public void Reinforce_DoesNotApplyToAZeroWeightedQueryToken()
    {
        SearchDocument[] corpus =
        {
            new("1", "chat", Category: "A"),
            new("2", "mail", Category: "B"),
        };

        var unreinforced = new NaiveBayesClassifier();
        unreinforced.Train(corpus);

        var reinforced = new NaiveBayesClassifier();
        reinforced.Train(corpus);
        reinforced.Reinforce("chat", "B", weight: 100.0);

        // Enough reinforcement to flip the query outright...
        WeightedToken[] chatAndMail = { WeightedToken.Full("chat"), WeightedToken.Full("mail") };
        Assert.Equal("A", unreinforced.Predict(chatAndMail, 1)[0].Category);
        Assert.Equal("B", reinforced.Predict(chatAndMail, 1)[0].Category);

        // ...and none of it reaching a query that zeroes the term, exactly as a zero-weighted
        // corpus term contributes nothing.
        WeightedToken[] zeroingChat = { new("chat", 0.0), WeightedToken.Full("mail") };
        Assert.Equal(unreinforced.Predict(zeroingChat, 1)[0], reinforced.Predict(zeroingChat, 1)[0]);
    }

    [Fact]
    public void Reinforce_AppliesUnderComplementScoring()
    {
        var classifier = Trained(options: new NaiveBayesOptions { Complement = true });

        classifier.Reinforce("forgotten password", "Billing", weight: 2.0);

        Assert.Equal("Billing", classifier.PredictBest("forgotten password"));
    }

    [Fact]
    public void Reinforce_RespectsExcludedCategories()
    {
        var classifier = Trained();
        classifier.Reinforce("invoice", "Support", weight: 4.0);

        Assert.Equal("Support", classifier.PredictBest("invoice"));

        var excluded = classifier.Predict(
            "invoice", limit: 10, excludedCategories: new HashSet<string> { "Support" });

        Assert.DoesNotContain(excluded, r => r.Category == "Support");
        Assert.Equal("Billing", excluded[0].Category);
    }

    // A retrain rebuilds the corpus model. It must not silently throw away the feedback the user
    // gave, as long as the category it was about still exists afterwards.
    [Fact]
    public void Train_LeavesTheLedgerIntact()
    {
        var classifier = Trained();
        classifier.Reinforce("invoice", "Support", weight: 4.0);

        classifier.Train(new[]
        {
            new SearchDocument("x", "invoice", Category: "Billing"),
            new SearchDocument("y", "invoice", Category: "Billing"),
            new SearchDocument("z", "chat", Category: "Support"),
        });

        // Support is no longer where "invoice" belongs, and the feedback still says it is.
        Assert.Equal("Support", classifier.PredictBest("invoice"));
    }

    // While the category is absent, its ledger entry is simply unreachable — the classifier cannot
    // name a label the corpus does not contain. The entry itself is not touched, so it applies
    // again if the category comes back. The rule is the simple one: Train never writes the ledger.
    [Fact]
    public void Train_ThatDropsTheCategory_LeavesItsLedgerEntryInert_AndItAppliesAgainOnReturn()
    {
        var classifier = Trained();
        classifier.Reinforce("invoice", "Support", weight: 4.0);

        classifier.Train(new[] { new SearchDocument("x", "invoice", Category: "Billing") });
        Assert.Equal("Billing", classifier.PredictBest("invoice"));

        classifier.Train(Corpus);
        Assert.Equal("Support", classifier.PredictBest("invoice"));

        // ForgetReinforcement is the way to say "I no longer mean that".
        classifier.ForgetReinforcement();
        Assert.Equal("Billing", classifier.PredictBest("invoice"));
    }

    // Unlearn retires a category when its last document goes, and it reaches the corpus model by a
    // different path than Train does. The ledger is untouched by either, so an entry for a retired
    // category stays unreachable and applies again if the category is re-learned -- the rule the
    // Train case above pins, reached here through Unlearn instead.
    [Fact]
    public void Unlearn_ThatRetiresTheCategory_LeavesItsLedgerEntryInert_AndItAppliesAgainOnReturn()
    {
        var classifier = Trained();
        classifier.Reinforce("invoice", "Support", weight: 4.0);
        Assert.Equal("Support", classifier.PredictBest("invoice"));

        // Support holds three documents; retracting them all retires the category with it.
        classifier.Unlearn(Corpus[0]);
        classifier.Unlearn(Corpus[1]);
        classifier.Unlearn(Corpus[2]);

        // Only Billing is left, and the classifier cannot name a label the corpus no longer holds.
        Assert.Equal("Billing", classifier.PredictBest("invoice"));

        // Support comes back on entirely different content, and the feedback still says what it said.
        classifier.Learn(new SearchDocument("z", "chat", Category: "Support"));
        Assert.Equal("Support", classifier.PredictBest("invoice"));

        classifier.ForgetReinforcement();
        Assert.Equal("Billing", classifier.PredictBest("invoice"));
    }

    // The ledger survives a category's retirement, which means it also survives a long absence.
    // That is the same deliberate rule, and it is also why ForgetReinforcement has to exist: it is
    // the only way to retire an entry whose category never comes back.
    [Fact]
    public void Unlearn_ThatRetiresTheCategory_KeepsTheLedgerEntryForReinforcingAgain()
    {
        var classifier = Trained();
        classifier.Reinforce("invoice", "Support", weight: 4.0);

        classifier.Unlearn(Corpus[0]);
        classifier.Unlearn(Corpus[1]);
        classifier.Unlearn(Corpus[2]);

        // Re-learn Support, forget the feedback, then assert it again: this is the "I changed my
        // mind" path, and it has to be reachable without retraining.
        classifier.Learn(new SearchDocument("z", "chat", Category: "Support"));
        classifier.ForgetReinforcement();
        Assert.Equal("Billing", classifier.PredictBest("invoice"));

        classifier.Reinforce("invoice", "Support", weight: 4.0);
        Assert.Equal("Support", classifier.PredictBest("invoice"));
    }

    [Fact]
    public void Reinforce_OnACategoryTheCorpusNeverSaw_IsIgnored()
    {
        var classifier = Trained();
        var before = Snapshot(classifier, "invoice");

        classifier.Reinforce("invoice", "Nonexistent", weight: 100.0);

        Assert.Equal(before, Snapshot(classifier, "invoice"));
    }

    [Fact]
    public void Reinforce_WithoutACategory_IsIgnored()
    {
        var classifier = Trained();
        var before = Snapshot(classifier, "invoice");

        classifier.Reinforce("invoice", string.Empty, weight: 10.0);
        classifier.Reinforce("invoice", null!, weight: 10.0);

        Assert.Equal(before, Snapshot(classifier, "invoice"));
    }

    [Fact]
    public void Reinforce_OnEmptyText_IsIgnored()
    {
        var classifier = Trained();
        var before = Snapshot(classifier, "invoice");

        classifier.Reinforce(string.Empty, "Support", weight: 10.0);
        classifier.Reinforce("   ", "Support", weight: 10.0);

        Assert.Equal(before, Snapshot(classifier, "invoice"));
    }

    [Fact]
    public void Reinforce_WithAZeroWeight_ChangesNothing()
    {
        var classifier = Trained();
        var before = Snapshot(classifier, "invoice");

        classifier.Reinforce("invoice", "Support", weight: 0.0);
        classifier.Unreinforce("invoice", "Support", weight: 0.0);

        Assert.Equal(before, Snapshot(classifier, "invoice"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Reinforce_RejectsANonFiniteWeight(double weight)
    {
        var classifier = Trained();

        Assert.Throws<ArgumentOutOfRangeException>(() => classifier.Reinforce("invoice", "Support", weight));
        Assert.Throws<ArgumentOutOfRangeException>(() => classifier.Unreinforce("invoice", "Support", weight));
    }

    [Fact]
    public void Reinforce_RejectsNullText()
    {
        var classifier = Trained();

        Assert.Throws<ArgumentNullException>(() => classifier.Reinforce(null!, "Support"));
        Assert.Throws<ArgumentNullException>(() => classifier.Unreinforce(null!, "Support"));
    }

    [Fact]
    public void ForgetReinforcement_OnAnUnreinforcedClassifier_IsHarmless()
    {
        var classifier = Trained();
        var before = Snapshot(classifier, "invoice");

        classifier.ForgetReinforcement();
        classifier.ForgetReinforcement();

        Assert.Equal(before, Snapshot(classifier, "invoice"));
    }

    [Fact]
    public void Reinforce_OnAnUntrainedClassifier_PredictsNothing()
    {
        var classifier = new NaiveBayesClassifier();
        classifier.Reinforce("invoice", "Support", weight: 1.0);

        // The label set is the corpus's. A category the model has never been trained on has no
        // prior and no class-conditional distribution, so reinforcement cannot conjure one.
        Assert.Empty(classifier.Predict("invoice"));
    }

    // Reference implementation, written out independently of the classifier: a class's log score
    // is the corpus model's score plus the sum over the query's terms of
    //   queryWeight * reinforcementIdf(term) * ledgerMass(category, term)
    // where reinforcementIdf is the model's own idf for a term the corpus has seen, and a flat 1.0
    // for a term it has not. Anything that changes those two numbers has to change this test.
    [Fact]
    public void Reinforce_Parity_WithAnIndependentScoringReference()
    {
        SearchDocument[] corpus =
        {
            new("1", "alpha beta", Category: "A"),
            new("2", "alpha gamma delta", Category: "A"),
            new("3", "beta gamma", Category: "B"),
            new("4", "delta epsilon", Category: "B"),
            new("5", "beta", Category: "C"),
        };

        const double alpha = 1.0;
        const double temperature = 0.75;

        var classifier = new NaiveBayesClassifier(
            options: new NaiveBayesOptions
            {
                IdfMode = IdfMode.DocumentCount,
                Alpha = alpha,
                SmoothPriors = true,
                Temperature = temperature,
            });
        classifier.Train(corpus);

        (string Text, string Category, double Weight)[] feedback =
        {
            ("alpha delta", "B", 1.5),   // both terms in the corpus, one in another class
            ("zeta", "C", 0.75),         // a term the corpus has never seen
            ("beta beta", "A", -0.5),    // a repeated term, counted once, and a negative weight
        };

        foreach (var (text, category, weight) in feedback)
            classifier.Reinforce(text, category, weight);

        WeightedToken[] query =
        {
            new("alpha", 1.0),
            new("beta", 0.6),
            new("zeta", 0.4),
        };

        var actual = classifier.Predict(query, limit: 5);
        var expected = ReferenceScores(corpus, feedback, query, alpha, temperature);

        Assert.Equal(3, expected.Count);
        Assert.Equal(expected.Count, actual.Count);
        foreach (var result in actual)
        {
            Assert.True(expected.TryGetValue(result.Category, out double reference),
                $"Category '{result.Category}' missing from the reference.");
            Assert.Equal(reference, result.Probability, precision: 10);
        }
    }

    private static Dictionary<string, double> ReferenceScores(
        IEnumerable<SearchDocument> corpus,
        (string Text, string Category, double Weight)[] feedback,
        WeightedToken[] query,
        double alpha,
        double temperature)
    {
        var classDocuments = new Dictionary<string, int>();
        var classTokens = new Dictionary<string, Dictionary<string, int>>();
        var classTokenTotals = new Dictionary<string, int>();
        var documentFrequency = new Dictionary<string, int>();
        var vocabulary = new HashSet<string>(StringComparer.Ordinal);
        var tokenize = (string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var document in corpus)
        {
            classDocuments.TryAdd(document.Category!, 0);
            classDocuments[document.Category!]++;
            classTokens.TryAdd(document.Category!, new Dictionary<string, int>(StringComparer.Ordinal));
            classTokenTotals.TryAdd(document.Category!, 0);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string term in tokenize(document.Text))
            {
                vocabulary.Add(term);
                classTokens[document.Category!].TryGetValue(term, out int count);
                classTokens[document.Category!][term] = count + 1;
                classTokenTotals[document.Category!]++;
                if (seen.Add(term))
                {
                    documentFrequency.TryGetValue(term, out int df);
                    documentFrequency[term] = df + 1;
                }
            }
        }

        // The ledger: signed mass per (category, distinct term).
        var ledger = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        foreach (var (text, category, weight) in feedback)
        {
            ledger.TryAdd(category, new Dictionary<string, double>(StringComparer.Ordinal));
            foreach (string term in tokenize(text).Distinct(StringComparer.Ordinal))
            {
                ledger[category].TryGetValue(term, out double mass);
                ledger[category][term] = mass + weight;
            }
        }

        int classCount = classDocuments.Count;
        int documentCount = classDocuments.Values.Sum();
        int vocabularySize = vocabulary.Count;

        var logScores = new Dictionary<string, double>();
        foreach (string category in classDocuments.Keys)
        {
            double score = Math.Log((classDocuments[category] + alpha) / (documentCount + (alpha * classCount)));
            double denominator = classTokenTotals[category] + (alpha * vocabularySize);

            foreach (var token in query)
            {
                bool known = documentFrequency.TryGetValue(token.Token, out int df) && df > 0;
                double idf = !known ? 1.0 : Math.Log(1.0 + (documentCount / (double)df));
                double reinforcementIdf = known ? idf : 1.0;

                classTokens[category].TryGetValue(token.Token, out int count);
                score += token.Weight * idf * Math.Log((count + alpha) / denominator);

                if (ledger.TryGetValue(category, out var mass) &&
                    mass.TryGetValue(token.Token, out double ledgerMass))
                {
                    score += token.Weight * reinforcementIdf * ledgerMass;
                }
            }

            logScores[category] = score;
        }

        double max = logScores.Values.Max();
        var exp = logScores.ToDictionary(kv => kv.Key, kv => Math.Exp((kv.Value - max) / temperature));
        double sum = exp.Values.Sum();
        return exp.ToDictionary(kv => kv.Key, kv => kv.Value / sum);
    }
}
