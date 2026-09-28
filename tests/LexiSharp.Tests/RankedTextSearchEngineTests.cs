using System.Diagnostics.CodeAnalysis;
using LexiSharp;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class RankedTextSearchEngineTests
{
    private static RankedTextSearchEngine CreateEngine(
        IEnumerable<SearchDocument> docs,
        ITextScorer? scorer = null)
    {
        var engine = new RankedTextSearchEngine(
            new InMemoryTextIndex(),
            scorer ?? new Bm25Scorer());
        engine.Index(docs);
        return engine;
    }

    private static readonly SearchDocument[] Docs =
    {
        new("1", "The search engine uses BM25 to rank the results"),
        new("2", "TF-IDF is a classic method of textual search"),
        new("3", "Italian cuisine is renowned in Rome"),
    };

    private static readonly string[] ABIds = new[] { "a", "b" };

    // Same term frequency, growing length: BM25's length normalization gives four distinct
    // scores, so the ranking (and its pages) is unambiguous.
    private static readonly SearchDocument[] PaginationDocs =
    {
        new("p1", "common filler filler filler"),
        new("p2", "common filler filler"),
        new("p3", "common filler"),
        new("p4", "common"),
    };

    [Fact]
    public void Search_ReturnsRelevantDocumentsFirst()
    {
        var engine = CreateEngine(Docs);

        var results = engine.Search("textual search", new SearchOptions(Limit: 10));

        Assert.Equal(2, results.Count);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public void Search_RespectsLimit()
    {
        var engine = CreateEngine(Docs);

        Assert.Single(engine.Search("search", new SearchOptions(Limit: 1)));
        Assert.Empty(engine.Search("search", new SearchOptions(Limit: 0)));
    }

    [Fact]
    public void Search_RespectsMinimumScore()
    {
        var engine = CreateEngine(Docs);

        var results = engine.Search("search", new SearchOptions(Limit: 10, MinimumScore: double.MaxValue));

        Assert.Empty(results);
    }

    [Fact]
    public void Search_BreaksScoreTiesByCorpusOrderAtTheLimit()
    {
        // All four documents match the boolean query with the exact same score (1).
        // The stable descending sort keeps the earliest-enumerated ones when the limit
        // cuts into a run of equal scores.
        var engine = CreateEngine(
            new[]
            {
                new SearchDocument("a", "shared alpha"),
                new SearchDocument("b", "shared beta"),
                new SearchDocument("g", "shared gamma"),
                new SearchDocument("d", "shared delta"),
            },
            new BooleanScorer(BooleanMatch.AnyTerm));

        var results = engine.Search("shared", new SearchOptions(Limit: 2));

        Assert.Equal(2, results.Count);
        Assert.Equal(1.0, results[0].Score);
        Assert.Equal(1.0, results[1].Score);
        Assert.Equal(ABIds, results.Select(r => r.DocumentId));
    }

    [Fact]
    public void Search_Offset_SkipsTheTopOfTheRanking()
    {
        var engine = CreateEngine(PaginationDocs);

        var all = engine.Search("common", new SearchOptions(Limit: 10));
        Assert.Equal(4, all.Count);

        var page = engine.Search("common", new SearchOptions(Limit: 2, Offset: 2));

        Assert.Equal(
            all.Skip(2).Take(2).Select(r => r.DocumentId),
            page.Select(r => r.DocumentId));
        Assert.Equal(
            all.Skip(2).Take(2).Select(r => r.Score),
            page.Select(r => r.Score));
    }

    [Fact]
    public void Search_OffsetPages_PartitionTheFullRanking()
    {
        var engine = CreateEngine(PaginationDocs);

        var all = engine.Search("common", new SearchOptions(Limit: 10));
        Assert.Equal(4, all.Count);

        var page1 = engine.Search("common", new SearchOptions(Limit: 2, Offset: 0));
        var page2 = engine.Search("common", new SearchOptions(Limit: 2, Offset: 2));
        var page3 = engine.Search("common", new SearchOptions(Limit: 2, Offset: 4));

        Assert.Equal(
            all.Select(r => r.DocumentId),
            page1.Concat(page2).Concat(page3).Select(r => r.DocumentId));
        Assert.Equal(2, page1.Count);
        Assert.Equal(2, page2.Count);
        Assert.Empty(page3);

        Assert.Empty(engine.Search("common", new SearchOptions(Limit: 2, Offset: 42)));
        Assert.Empty(engine.Search("common", new SearchOptions(Limit: 2, Offset: -1)));
    }

    [Fact]
    public void Search_OffsetAppliesAfterMinimumScoreFiltering()
    {
        var engine = CreateEngine(PaginationDocs);

        var all = engine.Search("common", new SearchOptions(Limit: 10));
        double threshold = all[1].Score;
        var filtered = all.Where(r => r.Score >= threshold).ToList();
        Assert.True(filtered.Count >= 2);

        // The page is cut from the *filtered* ranking: without the threshold the skip would
        // land on a different document.
        var page = engine.Search("common", new SearchOptions(
            Limit: 10, MinimumScore: threshold, Offset: 1));

        Assert.Equal(
            filtered.Skip(1).Select(r => r.DocumentId),
            page.Select(r => r.DocumentId));
    }

    [Fact]
    public void Search_WithOnlyStopWords_ReturnsNothing()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions { RemoveStopWords = true });
        var engine = new RankedTextSearchEngine(
            new InMemoryTextIndex(tokenizer),
            new Bm25Scorer(),
            tokenizer);
        engine.Index(Docs);

        Assert.Empty(engine.Search("the and of"));
    }

    [Fact]
    public void Search_NormalizesAccentsInQueryAndDocuments()
    {
        var engine = CreateEngine(Docs);
        var results = engine.Search("cuisine Rome");

        Assert.Single(results);
        Assert.Equal("3", results[0].DocumentId);
    }

    [Fact]
    public void Add_Remove_AreReflectedInSearch()
    {
        var engine = CreateEngine(Docs);

        engine.Remove("1");

        Assert.DoesNotContain(engine.Search("engine").Select(r => r.DocumentId), id => id == "1");

        engine.Add(new SearchDocument("4", "a powerful electric engine"));

        Assert.Contains(engine.Search("engine").Select(r => r.DocumentId), id => id == "4");
    }

    [Fact]
    public void Clear_DropsEverything()
    {
        var engine = CreateEngine(Docs);

        engine.Clear();

        Assert.Empty(engine.Search("search"));
    }

    [Fact]
    public void Search_WorksWithSwappedScorers()
    {
        var scaffolder = new Func<ITextScorer, ITextSearchEngine>(scorer =>
            CreateEngine(Docs, scorer));

        var engines = new (ITextSearchEngine Engine, string Name)[]
        {
            (scaffolder(new Bm25Scorer()), "bm25"),
            (scaffolder(new TfIdfScorer()), "tfidf"),
            (scaffolder(new BooleanScorer(BooleanMatch.AnyTerm)), "bool"),
            (scaffolder(new QueryLikelihoodScorer()), "ql"),
        };

        foreach (var (engine, _) in engines)
        {
            var results = engine.Search("textual search");
            Assert.Equal(2, results.Count);
        }
    }

    [Fact]
    public void Search_EngineAndIndexShareTokenizerConfiguration()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions { Stemmer = new SuffixStrippingStemmer() });
        var engine = new RankedTextSearchEngine(
            new InMemoryTextIndex(tokenizer),
            new Bm25Scorer(),
            tokenizer);

        engine.Add(new SearchDocument("1", "searching searching searching"));

        var results = engine.Search("search");

        Assert.Single(results);
    }

    private static readonly SearchDocument[] PhraseDocs =
    {
        new("adjacent", "machine learning systems"),
        new("reversed", "learning machine systems"),
        new("gapped", "machine that learning systems"),
        new("other", "completely unrelated text"),
    };

    [Fact]
    public void Search_PhraseQuery_RequiresConsecutivePositions()
    {
        var engine = CreateEngine(PhraseDocs);

        var results = engine.Search("\"machine learning\"", new SearchOptions(Limit: 10));

        var hit = Assert.Single(results);
        Assert.Equal("adjacent", hit.DocumentId);
        Assert.True(hit.Score > 0);
    }

    [Fact]
    public void Search_MixedQuery_PhraseGatesCorpusButFreeTermsOnlyScore()
    {
        var engine = CreateEngine(new[]
        {
            new SearchDocument("phrase-only", "machine learning systems rock"),
            new SearchDocument("both", "neural machine learning systems"),
            new SearchDocument("free-only", "neural networks accelerate fast"),
            new SearchDocument("reversed", "learning machine neural systems"),
        });

        var results = engine.Search("neural \"machine learning\"", new SearchOptions(Limit: 10));

        // The free term alone never pulls a document in, and never keeps one out: "phrase-only"
        // lacks "neural" yet matches through the phrase; "free-only" has "neural" but no phrase.
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.DocumentId == "phrase-only");
        Assert.Contains(results, r => r.DocumentId == "both");
        Assert.DoesNotContain(results, r => r.DocumentId == "free-only");
        Assert.DoesNotContain(results, r => r.DocumentId == "reversed");
    }

    [Fact]
    public void Search_MultiplePhrases_AreAnded()
    {
        var engine = CreateEngine(new[]
        {
            new SearchDocument("both", "deep learning and machine learning"),
            new SearchDocument("first-only", "deep learning beats statistics"),
            new SearchDocument("second-only", "machine learning beats statistics"),
        });

        var results = engine.Search("\"deep learning\" \"machine learning\"", new SearchOptions(Limit: 10));

        var hit = Assert.Single(results);
        Assert.Equal("both", hit.DocumentId);
    }

    [Fact]
    public void Search_SingleTokenPhrase_MatchesTermPresence()
    {
        var engine = CreateEngine(Docs);

        var results = engine.Search("\"search\"", new SearchOptions(Limit: 10));

        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, r => r.DocumentId == "3");
    }

    [Fact]
    public void Search_OnlyVacuousPhrases_ReturnsNothing()
    {
        var engine = CreateEngine(Docs);

        Assert.Empty(engine.Search("\"\"", new SearchOptions(Limit: 10)));
        Assert.Empty(engine.Search("\"   \"", new SearchOptions(Limit: 10)));
    }

    [Fact]
    public void Search_PrefixAtom_ExpandsAgainstTheVocabulary()
    {
        var engine = CreateEngine(new[]
        {
            new SearchDocument("1", "neural networks rule"),
            new SearchDocument("2", "neuralnets are tall"),
            new SearchDocument("3", "unrelated content here"),
        });

        var results = engine.Search("neural*", new SearchOptions(Limit: 10));

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.DocumentId == "1");
        Assert.Contains(results, r => r.DocumentId == "2");
    }

    [Fact]
    public void Search_PrefixAtom_WithPlainTerms_ScoreOnlyBothSides()
    {
        var engine = CreateEngine(new[]
        {
            new SearchDocument("1", "neural networks rule"),
            new SearchDocument("2", "neuralnets are tall"),
            new SearchDocument("3", "networks effects"),
        });

        // Free terms (here: the expansion and the literal) never hard-filter: every document
        // sharing at least one of them scores above zero.
        var results = engine.Search("neural* networks", new SearchOptions(Limit: 10));

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void Search_FuzzyAtom_MatchesWithinEditDistance()
    {
        var engine = CreateEngine(new[]
        {
            new SearchDocument("1", "cars park"),
            new SearchDocument("2", "a car parks"),
            new SearchDocument("3", "care costs"),
            new SearchDocument("4", "truck loads"),
        });

        // Default budget 1: "cars" (0 edit), "car" and "care" (1 edit) all expand in.
        var fuzzy = engine.Search("cars~", new SearchOptions(Limit: 10));
        Assert.Equal(new[] { "1", "2", "3" }, fuzzy.Select(r => r.DocumentId).OrderBy(x => x));

        // Budget 0 pins the expansion to the exact term.
        var exact = engine.Search("cars~0", new SearchOptions(Limit: 10));
        var hit = Assert.Single(exact);
        Assert.Equal("1", hit.DocumentId);

        // Without the operator the typo/exact term is looked up literally.
        var literal = engine.Search("cars", new SearchOptions(Limit: 10));
        Assert.Single(literal);
    }

    [Fact]
    public void Search_FuzzyAtom_TypoInQueryFindsTheCorrectedTerm()
    {
        var engine = CreateEngine(new[]
        {
            new SearchDocument("1", "neural networks"),
            new SearchDocument("2", "unrelated stuff"),
        });

        var results = engine.Search("neurall~", new SearchOptions(Limit: 10));

        var hit = Assert.Single(results);
        Assert.Equal("1", hit.DocumentId);

        // Same typo without the operator matches nothing.
        Assert.Empty(engine.Search("neurall"));
    }

    [Fact]
    public void Search_FuzzyAtom_OutOfVocabOnlyKeepsInVocabularyTermsExact()
    {
        var engine = CreateEngine(new[]
        {
            new SearchDocument("1", "keycloak login fails"),
            new SearchDocument("2", "keycloaks proxy setup"),
            new SearchDocument("3", "unrelated stuff"),
        });

        // Default behavior expands the known term to its close variants too.
        var expanded = engine.Search("keycloak~", new SearchOptions(Limit: 10));
        Assert.Equal(new[] { "1", "2" }, expanded.Select(r => r.DocumentId).OrderBy(x => x));

        // With the flag, an in-vocabulary base term matches exactly: doc 2 ("keycloaks") falls out.
        var exactOnly = engine.Search("keycloak~", new SearchOptions(Limit: 10, FuzzyOnlyOutOfVocabulary: true));
        var hit = Assert.Single(exactOnly);
        Assert.Equal("1", hit.DocumentId);

        // The typo stays correctable: "keycloack" is out of vocabulary, so fuzzy expansion applies.
        var typo = engine.Search("keycloack~", new SearchOptions(Limit: 10, FuzzyOnlyOutOfVocabulary: true));
        Assert.Equal("1", Assert.Single(typo).DocumentId);

        // Prefix atoms are never affected by the flag.
        var prefix = engine.Search("keyclo*", new SearchOptions(Limit: 10, FuzzyOnlyOutOfVocabulary: true));
        Assert.Equal(new[] { "1", "2" }, prefix.Select(r => r.DocumentId).OrderBy(x => x));
    }

    [Fact]
    public void Search_PrefixExpansion_CapsTermsPerAtom()
    {
        var docs = new SearchDocument[70];
        for (int i = 0; i < docs.Length; i++)
            docs[i] = new SearchDocument($"d{i}", $"zpad{i:D2}");

        var engine = CreateEngine(docs);

        var results = engine.Search("zpad*", new SearchOptions(Limit: 100));

        // All document frequencies tie, so ordinal order decides: zpad00..zpad63 win the cap.
        Assert.Equal(RankedTextSearchEngine.MaxExpansionsPerAtom, results.Count);
        Assert.Contains(results, r => r.DocumentId == "d0");
        Assert.DoesNotContain(results, r => r.DocumentId == "d64");
    }

    [Fact]
    public void Search_ExpansionOnIndexWithoutVocabulary_FallsBackToLiteralBase()
    {
        var inner = new InMemoryTextIndex();
        inner.Index(new[]
        {
            new SearchDocument("1", "neural networks"),
            new SearchDocument("2", "neuralnets are tall"),
        });

        var engine = new RankedTextSearchEngine(new NoVocabularyIndex(inner), new Bm25Scorer());

        var results = engine.Search("neural*", new SearchOptions(Limit: 10));

        var hit = Assert.Single(results);
        Assert.Equal("1", hit.DocumentId);
    }

    [Fact]
    public void Search_ExpansionOperatorInsideQuotesIsLiteral()
    {
        var engine = CreateEngine(new[]
        {
            new SearchDocument("1", "machine learning rocks"),
            new SearchDocument("2", "machines learn rock"),
        });

        var results = engine.Search("\"machine*\"", new SearchOptions(Limit: 10));

        // Quoted segments never expand: only the exact term "machine" satisfies the phrase.
        var hit = Assert.Single(results);
        Assert.Equal("1", hit.DocumentId);
    }

    [Fact]
    public void Engine_Explain_ResolvesExpansionTerms()
    {
        var engine = CreateEngine(new[] { new SearchDocument("1", "neural networks") });

        var withOperator = engine.Explain("1", "neural*");
        var literal = engine.Explain("1", "neural");

        Assert.NotNull(withOperator);
        Assert.NotNull(literal);
        Assert.Equal(literal!.TotalScore, withOperator!.TotalScore, 12);
    }

    private static RankedTextSearchEngine CreateEngineWithSynonyms(
        IEnumerable<SearchDocument> docs,
        SynonymMap synonyms)
    {
        var engine = new RankedTextSearchEngine(
            new InMemoryTextIndex(),
            new Bm25Scorer(),
            synonyms: synonyms);
        engine.Index(docs);
        return engine;
    }

    [Fact]
    public void Search_OneWaySynonym_MatchesTheTargetButNotTheReverse()
    {
        var docs = new[]
        {
            new SearchDocument("1", "the automobile is fast"),
            new SearchDocument("2", "a car parked outside"),
            new SearchDocument("3", "unrelated content here"),
        };
        var synonyms = new SynonymMap().Add("car", "automobile");

        var forward = CreateEngineWithSynonyms(docs, synonyms).Search("car", new SearchOptions(Limit: 10));
        Assert.Equal(2, forward.Count);
        Assert.Contains(forward, r => r.DocumentId == "1");
        Assert.Contains(forward, r => r.DocumentId == "2");

        // One-way: "automobile" does not pull "car" back in.
        var reverse = CreateEngineWithSynonyms(docs, synonyms).Search("automobile", new SearchOptions(Limit: 10));
        var hit = Assert.Single(reverse);
        Assert.Equal("1", hit.DocumentId);
    }

    [Fact]
    public void Search_EquivalenceGroup_MatchesEveryMemberBothWays()
    {
        var docs = new[]
        {
            new SearchDocument("1", "a car parked"),
            new SearchDocument("2", "an automobile parked"),
            new SearchDocument("3", "an auto parked"),
            new SearchDocument("4", "a truck parked"),
        };
        var synonyms = new SynonymMap().AddEquivalent("car", "automobile", "auto");

        foreach (var query in new[] { "car", "automobile", "auto" })
        {
            var results = CreateEngineWithSynonyms(docs, synonyms)
                .Search(query, new SearchOptions(Limit: 10));

            Assert.Equal(
                new[] { "1", "2", "3" },
                results.Select(r => r.DocumentId).OrderBy(x => x, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Search_SynonymExpansion_IsOneLevelAndNonTransitive()
    {
        var docs = new[]
        {
            new SearchDocument("1", "alpha here"),
            new SearchDocument("2", "beta here"),
            new SearchDocument("3", "gamma here"),
        };
        var synonyms = new SynonymMap()
            .Add("alpha", "beta")
            .Add("beta", "gamma");

        var fromAlpha = CreateEngineWithSynonyms(docs, synonyms)
            .Search("alpha", new SearchOptions(Limit: 10));
        Assert.Equal(2, fromAlpha.Count); // alpha + beta, never gamma

        var fromBeta = CreateEngineWithSynonyms(docs, synonyms)
            .Search("beta", new SearchOptions(Limit: 10));
        Assert.Equal(2, fromBeta.Count); // beta + gamma, never alpha (edge was one-way)
    }

    [Fact]
    public void Search_Synonyms_DoNotApplyInsidePhrases()
    {
        var docs = new[]
        {
            new SearchDocument("1", "a fast car here"),
            new SearchDocument("2", "a fast auto here"),
        };
        var synonyms = new SynonymMap().AddEquivalent("car", "auto");

        var engine = CreateEngineWithSynonyms(docs, synonyms);

        // The phrase gate uses the literal phrase terms: "auto" does not satisfy "car".
        var results = engine.Search("\"fast car\"", new SearchOptions(Limit: 10));
        var hit = Assert.Single(results);
        Assert.Equal("1", hit.DocumentId);

        // The free term around it does expand.
        var free = engine.Search("fast car", new SearchOptions(Limit: 10));
        Assert.Equal(2, free.Count);
    }

    [Fact]
    public void Constructor_SynonymEntryNotReducingToOneTerm_Throws()
    {
        var index = new InMemoryTextIndex();

        var multiToken = Assert.Throws<ArgumentException>(() =>
            new RankedTextSearchEngine(index, new Bm25Scorer(), synonyms: new SynonymMap().Add("car engine", "motor")));
        Assert.Contains("car engine", multiToken.Message);

        // A single character is dropped by the default tokenizer: zero terms, same rejection.
        Assert.Throws<ArgumentException>(() =>
            new RankedTextSearchEngine(index, new Bm25Scorer(), synonyms: new SynonymMap().Add("x", "motor")));

        Assert.Throws<ArgumentException>(() =>
            new RankedTextSearchEngine(index, new Bm25Scorer(), synonyms: new SynonymMap().AddEquivalent("only")));
    }

    [Fact]
    public void Constructor_EmptySynonymMap_BehavesLikeNoMap()
    {
        var engine = new RankedTextSearchEngine(
            new InMemoryTextIndex(),
            new Bm25Scorer(),
            synonyms: new SynonymMap());
        engine.Index(Docs);

        Assert.Equal(2, engine.Search("textual search", new SearchOptions(Limit: 10)).Count);
    }

    [Fact]
    public void Engine_Explain_UsesTheSameSynonymExpansion()
    {
        var docs = new[] { new SearchDocument("1", "an auto parked") };
        var engine = CreateEngineWithSynonyms(docs, new SynonymMap().Add("car", "auto"));

        var viaSynonym = engine.Explain("1", "car");
        var literal = engine.Explain("1", "auto");

        Assert.NotNull(viaSynonym);
        Assert.NotNull(literal);
        Assert.Equal(literal!.TotalScore, viaSynonym!.TotalScore, 12);
    }

    /// <summary>An <see cref="ITextIndex"/> without <see cref="IVocabularyIndex"/> — exercises the literal-fallback path.</summary>
    private sealed class NoVocabularyIndex : ITextIndex
    {
        private readonly InMemoryTextIndex _inner;

        public NoVocabularyIndex(InMemoryTextIndex inner) => _inner = inner;

        public IReadOnlyCollection<SearchDocument> Documents => _inner.Documents;

        public int Count => _inner.Count;

        public double AverageDocumentLength => _inner.AverageDocumentLength;

        public int VocabularySize => _inner.VocabularySize;

        public long CorpusTokenCount => _inner.CorpusTokenCount;

        public void Index(IEnumerable<SearchDocument> documents) => _inner.Index(documents);

        public void Add(SearchDocument document) => _inner.Add(document);

        public bool Remove(string documentId) => _inner.Remove(documentId);

        public void Clear() => _inner.Clear();

        public bool Contains(string documentId) => _inner.Contains(documentId);

        public IReadOnlyList<string> GetTerms(string documentId) => _inner.GetTerms(documentId);

        public IReadOnlyList<int> GetTermPositions(string documentId, string term) =>
            _inner.GetTermPositions(documentId, term);

        public int DocumentFrequency(string term) => _inner.DocumentFrequency(term);

        public int CorpusFrequency(string term) => _inner.CorpusFrequency(term);

        public int TermFrequency(string documentId, string term) =>
            _inner.TermFrequency(documentId, term);

        public int DocumentLength(string documentId) => _inner.DocumentLength(documentId);

        public bool TryGetDocument(
            string documentId,
            [NotNullWhen(true)] out SearchDocument? document) =>
            _inner.TryGetDocument(documentId, out document);
    }
}