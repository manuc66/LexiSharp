using System;
using System.Linq;
using LexiSharp.Benchmarking;
using LexiSharp.Core;
using LexiSharp.Expansion;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class SemanticLexicalIndexTests
{
    private static readonly SearchDocument[] TokenCorpus =
    [
        new("d1", "the oauth access token expires and triggers a refresh of the session"),
        new("d2", "refresh your oauth access token with the dedicated endpoint"),
        new("d3", "token refresh keeps the session alive for another hour"),
        new("d4", "an expired token forces the server to refresh the whole authentication flow"),
        new("d5", "the oauth token expiry loop blocks every api request"),
        new("d6", "the session expires and the client renews it through a refresh token"),
        new("d7", "authentication relies on a short lived access token with an expiry instant"),
        new("d8", "the refresh endpoint mints a new access token when the current one expires"),
        new("d9", "the weather in paris is sunny this week"),
        new("d10", "coffee prices rose sharply this morning in the market"),
        new("d11", "the garden sprinkler waters the tulips every morning"),
        new("d12", "bicycles share the path with pedestrians during rush hour"),
        new("d13", "the baker kneads the dough on a wooden table"),
        new("d14", "the library opens at nine and closes at six on weekdays"),
        new("d15", "the train departs from the central station at dawn"),
        new("d16", "the chef adds salt and pepper to the simmering stew"),
        new("d17", "the orchestra performs a symphony in the old opera house"),
        new("d18", "the hikers cross the ridge before the storm arrives in the valley"),
        new("d19", "the printer prints the report on white paper"),
        new("d20", "the children build a castle from the sand on the beach"),
        new("d21", "the gardener prunes the roses in the early morning"),
        new("d22", "the astronomer watches the stars through a telescope on the hill"),
        new("d23", "the potter shapes the clay on a spinning wheel"),
        new("d24", "the mechanic fixes the engine and checks the oil level"),
        new("d25", "the tailor sews the fabric with thin needles"),
        new("d26", "the pilot lands the plane on the runway at dusk"),
        new("d27", "the teacher wrote the lesson on the blackboard with chalk"),
        new("d28", "the musician tunes the guitar before the concert begins"),
    ];

    private static PmiTermExpander Learn(SearchDocument[]? corpus = null, PmiTermExpanderOptions? options = null)
        => PmiTermExpander.LearnFrom(corpus ?? TokenCorpus, options: options);

    [Fact]
    public void PmiTermExpander_DiscoversConceptualAssociations()
    {
        var expander = Learn();

        var expansion = expander.Expand(["token"]).ToList();

        Assert.Contains(expansion, term => term.Term == "refresh");
        Assert.All(expansion, term => Assert.InRange(term.Weight, 0.0, 1.0));
        Assert.DoesNotContain(expansion, term => term.Term == "token");
    }

    [Fact]
    public void PmiTermExpander_NeverEchoesInputs_AndRespectsCaps()
    {
        var expander = Learn(options: new PmiTermExpanderOptions { MaxTermsPerInputTerm = 2, MaxTotalTerms = 4 });

        var expansion = expander.Expand(["token", "refresh", "expiry"]).ToList();

        Assert.All(expansion, term => Assert.DoesNotContain(term.Term, new[] { "token", "refresh", "expiry" }));
        Assert.InRange(expansion.Count, 1, 4);
        Assert.Equal(expansion.Count, expansion.Select(term => term.Term).Distinct().Count());
    }

    [Fact]
    public void PmiTermExpander_EmptyCorpus_YieldsNoExpansion()
    {
        var expander = PmiTermExpander.LearnFrom(Array.Empty<SearchDocument>());

        Assert.Empty(expander.Expand(["token"]));
    }

    [Fact]
    public void AddExpansionTerms_InjectsTermsAtGappedPositions()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("x", "the oauth token expiry loop")); // 5 tokens

        Assert.Equal(0, index.TermFrequency("x", "refresh"));
        Assert.DoesNotContain("refresh", index.GetTerms("x"));

        index.AddExpansionTerms("x", ["refresh"]);

        Assert.Equal(1, index.TermFrequency("x", "refresh"));
        Assert.All(index.GetTermPositions("x", "refresh"), position => Assert.True(position >= 7)); // 5 + gap 2
        Assert.Equal(6, index.DocumentLength("x"));
        Assert.Equal(6, index.GetTerms("x").Count);

        Assert.Throws<KeyNotFoundException>(() => index.AddExpansionTerms("unknown", ["x"]));
    }

    [Fact]
    public void ExpansionTextIndex_KeepsOriginalDocumentsIntact()
    {
        var expander = Learn();
        var index = new ExpansionTextIndex(expander);

        index.Add(TokenCorpus[4]);

        Assert.True(index.TryGetDocument("d5", out var stored));
        Assert.Equal(TokenCorpus[4].Text, stored!.Text);
        Assert.Contains("refresh", index.GetTerms("d5"));
    }

    [Fact]
    public void Engine_WithSemanticExpansion_MatchesConceptuallyRelatedDocuments()
    {
        var baseIndex = new InMemoryTextIndex();
        baseIndex.Index(TokenCorpus);
        var baseEngine = new RankedTextSearchEngine(baseIndex, new Bm25Scorer());
        Assert.DoesNotContain(baseEngine.Search("refresh"), result => result.DocumentId == "d5");

        var semanticIndex = new ExpansionTextIndex(Learn());
        semanticIndex.Index(TokenCorpus);
        var semanticEngine = new RankedTextSearchEngine(semanticIndex, new Bm25Scorer());

        var hits = semanticEngine.Search("refresh");
        Assert.Contains(hits, result => result.DocumentId == "d5");
        Assert.DoesNotContain(hits, result => result.DocumentId == "d9"); // unrelated doc stays out
    }

    [Fact]
    public void Engine_PhraseQueries_NeverBridgeIntoExpansionTerms()
    {
        var extend = TokenCorpus.Append(new SearchDocument("d8", "token refresh abc")).ToArray();
        var semanticIndex = new ExpansionTextIndex(Learn(extend));
        semanticIndex.Index(extend);

        var phraseHits = semanticEngineLike(semanticIndex).Search("\"token refresh\"");

        Assert.Contains(phraseHits, result => result.DocumentId == "d8");
        Assert.DoesNotContain(phraseHits, result => result.DocumentId == "d5"); // refresh comes from expansion only

        static RankedTextSearchEngine semanticEngineLike(ExpansionTextIndex index)
            => new(index, new Bm25Scorer());
    }

    [Fact]
    public void Facade_TermExpander_ReachesDocumentsBeyondLiteralTerms()
    {
        var expander = Learn();

        var index = new LexiSharpIndex<SearchDocument>(options =>
        {
            options.TermExpander = expander;
            options.UseBm25();
        });
        index.AddRange(TokenCorpus);

        var hits = index.Search("refresh");

        Assert.Contains(hits, hit => hit.DocumentId == "d5");
        Assert.DoesNotContain(hits, hit => hit.DocumentId == "d9");
    }

    [Fact]
    public void Benchmark_Bm25Semantic_RunsAndReportsMetrics()
    {
        var queries = new[]
        {
            new BenchmarkQuery("q1", "refresh", ["d1", "d2", "d3", "d4", "d5"]),
            new BenchmarkQuery("q2", "sunny weather", ["d6"]),
        };

        var results = CorpusBenchmark.Run(TokenCorpus, queries, [BenchmarkConfig.Bm25Semantic()], new BenchmarkOptions { TopK = 5 });

        var config = Assert.Single(results);
        Assert.Equal("BM25 + semantic", config.Name);
        Assert.Equal(2, config.JudgedQueries);
        Assert.True(config.Metrics.RecallAtK > 0);
    }

    /// <summary>
    /// Two candidates with the same co-occurrence count are ordered by mutual information.
    /// </summary>
    /// <remarks>
    /// The mutual information was computed, used as a filter, and then dropped, so every stored
    /// score was zero and the tiebreak the sort documents compared zero with zero: it could never
    /// decide anything. A count tie is the only place the tiebreak is reachable, so this is the one
    /// construction that reaches it. "rare" co-occurs with the seed term and nowhere else, so its
    /// association is strong; "common" co-occurs with the seed term the same number of times but
    /// also appears in windows that do not contain it, so its association is weaker. The counts tie,
    /// and the stronger association is the one that has to come first.
    /// </remarks>
    [Fact]
    public void Expand_BreaksACountTieByMutualInformation()
    {
        var corpus = new List<SearchDocument>();
        int index = 0;

        // Eight tokens a document, so the window of eight covers all of it and the corpus holds one
        // window per document. The filler tokens are unique per document, and that is what keeps the
        // test to two candidates: a filler shared across documents co-occurs with the seed term
        // exactly as often as "rare" does, carries the same mutual information, and wins the
        // alphabetical tiebreak that ends the ordering.
        for (int i = 0; i < 3; i++)
            corpus.Add(new SearchDocument($"r{index++}", $"a{i} b{i} seed rare c{i} d{i} e{i} f{i}"));

        // Three where the seed term sits beside "common": the same co-occurrence count.
        for (int i = 0; i < 3; i++)
            corpus.Add(new SearchDocument($"c{index++}", $"g{i} h{i} seed common i{i} j{i} k{i} l{i}"));

        // Two more windows with "common" and no seed term, which is what starts to separate the two
        // candidates' mutual information.
        for (int i = 0; i < 2; i++)
            corpus.Add(new SearchDocument($"m{index++}", $"m{i} n{i} common o{i} p{i} q{i} r{i} s{i}"));

        // Four windows containing neither, and they are load-bearing rather than padding. A
        // candidate's association is log(count x windows / seedWindows x candidateWindows), so
        // "common" has a positive association only while the corpus holds more than twice its own
        // window count; without these four it is either cut as too common or scores zero mutual
        // information, and the tie this test needs never happens.
        for (int i = 0; i < 4; i++)
            corpus.Add(new SearchDocument($"n{index++}", $"t{i} u{i} v{i} w{i} x{i} y{i} z{i} aa{i}"));

        var expander = PmiTermExpander.LearnFrom(corpus, options: new PmiTermExpanderOptions
        {
            MaxTermsPerInputTerm = 2,
            MaxTotalTerms = 2,
        });

        var expanded = expander.Expand(["seed"]).ToList();

        Assert.Equal(2, expanded.Count);
        Assert.Equal("rare", expanded[0].Term);
        Assert.Equal("common", expanded[1].Term);
    }

}
