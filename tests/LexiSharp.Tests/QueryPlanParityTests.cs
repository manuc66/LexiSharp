using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class QueryPlanParityTests
{
    private static readonly string[] Corpus =
    {
        "the quick brown fox jumps over the lazy dog",
        "a pangram is a sentence using every letter of the alphabet at least once",
        "lexical search engines rank documents by term statistics",
        "bm25 blends term saturation with document length normalization",
        "query likelihood uses jelinek-mercer smoothing",
        "tf idf weights rare terms more heavily than frequent ones",
        "the universal answer is forty two",
        "inverted indexes map every term to the documents containing it",
        "candidatemark", // shared by several generated queries below
    };

    [Theory]
    [InlineData("bm25")]
    [InlineData("bm25-tuned")]
    [InlineData("tfidf")]
    [InlineData("ql")]
    [InlineData("ql-tuned")]
    [InlineData("bm25f")]
    [InlineData("bm25f-weighted")]
    public void Plan_MatchesScore_Bitwise(string scorerKind)
    {
        var scorer = Build(scorerKind);
        var index = BuildIndex();

        foreach (var query in DistinctQueries())
        {
            var tokens = index.Tokenizer.Tokenize(query);
            var plan = ((IQueryPlannableScorer)scorer).CreatePlan(tokens, index);

            foreach (var documentId in index.Documents.Select(d => d.Id))
            {
                double expected = scorer.Score(documentId, tokens, index);
                double actual = plan.Score(documentId);

                Assert.Equal(expected, actual);
            }
        }
    }

    /// <summary>
    /// The term-at-a-time pass must produce the identical <c>double</c> the per-document loop
    /// produces, for every document — not merely the same ranking.
    /// </summary>
    /// <remarks>
    /// This is the load-bearing test of the accumulation path. The two loops sum the query terms
    /// in the same order, so they ought to agree bit for bit; and they have to, because
    /// <c>TopRankedWindow</c> breaks ties with <c>==</c> on the score, so a last-bit difference
    /// between two documents reorders the page. A tolerance comparison here would let exactly the
    /// regression this guards against through.
    /// <para>
    /// It also pins that a document the terms never reach comes out of the accumulator as a
    /// non-match, which is what lets the engine treat the touched set as the match set.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("bm25")]
    [InlineData("bm25-tuned")]
    [InlineData("bm25k0")]
    [InlineData("bm25b0")]
    [InlineData("bm25b1")]
    [InlineData("tfidf")]
    [InlineData("bm25plus")]
    [InlineData("bm25plus-tuned")]
    [InlineData("bm25l")]
    [InlineData("bm25l-tuned")]
    public void AccumulatingPlan_MatchesScore_Bitwise(string scorerKind)
    {
        var scorer = Build(scorerKind);
        var index = BuildIndex();
        var accumulatingIndex = Assert.IsAssignableFrom<IAccumulatingIndex>(index);

        foreach (var query in Queries())
        {
            var tokens = index.Tokenizer.Tokenize(query);
            var plan = ((IQueryPlannableScorer)scorer).CreatePlan(tokens, index);
            var accumulatingPlan = Assert.IsAssignableFrom<IAccumulatingQueryPlan>(plan);

            using var accumulator = ScoreAccumulator.Rent(accumulatingIndex.OrdinalSpace);
            Assert.True(accumulatingPlan.TryAccumulate(accumulatingIndex, accumulator));

            // Every document the per-document path scores non-zero must appear exactly once, with
            // the same double; every document it scores 0 must be absent from the touched set.
            var touched = new Dictionary<int, double>();

            for (int i = 0; i < accumulator.Count; i++)
            {
                int ordinal = accumulator.OrdinalAt(i);
                Assert.False(touched.ContainsKey(ordinal));
                touched[ordinal] = accumulator[ordinal];
            }

            int ordinalOf = 0;

            foreach (var document in index.Documents)
            {
                double expected = plan.Score(document.Id);
                bool isMatch = expected != 0;

                Assert.Equal(
                    isMatch,
                    touched.TryGetValue(ordinalOf, out double actual));

                if (isMatch)
                    Assert.Equal(expected, actual);

                ordinalOf++;
            }

            // Reusing the buffer must give the same answer as the first use: the recorded flags
            // are the only state a pass leaves behind, and clearing them is what makes reuse
            // correct.
            accumulator.Reset();
            Assert.Equal(0, accumulator.Count);

            Assert.True(accumulatingPlan.TryAccumulate(accumulatingIndex, accumulator));
            Assert.Equal(touched.Count, accumulator.Count);

            for (int i = 0; i < accumulator.Count; i++)
                Assert.Equal(touched[accumulator.OrdinalAt(i)], accumulator[accumulator.OrdinalAt(i)]);
        }
    }

    /// <summary>
    /// The two scoring paths must also agree on what the <i>engine</i> returns, page for page,
    /// including the order exact ties come out in.
    /// </summary>
    /// <remarks>
    /// The bit-parity test above proves the arithmetic. This one proves the engine takes both
    /// paths and lands in the same place — a metadata filter is one of the two conditions that
    /// send a query down the per-document loop, so adding a filter that passes everything is an
    /// A/B of the two loops through the public surface, with no internal type in the assertion.
    /// </remarks>
    [Theory]
    [InlineData("bm25")]
    [InlineData("tfidf")]
    [InlineData("bm25plus")]
    [InlineData("bm25l")]
    public void Engine_ResultsAreIdenticalWhicheverScoringPathItTakes(string scorerKind)
    {
        // Deliberately full of exact ties: "ranking" appears once everywhere and twice in a third
        // of the documents, so a large block of the corpus scores identically and the page is
        // decided by the tie-break alone.
        var documents = Enumerable.Range(0, 400).Select(i => new SearchDocument(
            $"doc-{i:D4}",
            $"term statistics {(i % 3 == 0 ? "ranking ranking" : "ranking")} filler",
            new Dictionary<string, string> { ["category"] = i % 2 == 0 ? "a" : "b" },
            TextFields: new Dictionary<string, string> { ["title"] = "term filler" })).ToList();

        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), Build(scorerKind));
        engine.Index(documents);

        var passesEverything = new[] { new MetadataFilter("category", MetadataFilterOperator.NotEqual, "zzz") };

        foreach (var query in new[] { "term", "term statistics", "filler", "term ranking" })
        {
            foreach (var option in new[]
            {
                new SearchOptions(Limit: 25),
                new SearchOptions(Limit: 10, Offset: 7),
                new SearchOptions(Limit: 400),
            })
            {
                var accumulated = engine.Search(query, option);
                var perDocument = engine.Search(query, option with { Filters = passesEverything });

                Assert.Equal(perDocument.Select(r => r.DocumentId), accumulated.Select(r => r.DocumentId));
                Assert.Equal(perDocument.Select(r => r.Score), accumulated.Select(r => r.Score));
            }
        }
    }

    private static InMemoryTextIndex BuildIndex()
    {
        var index = new InMemoryTextIndex();
        index.Index(Corpus.Select((text, i) => new SearchDocument(
            i.ToString(), text,
            TextFields: new Dictionary<string, string>
            {
                ["title"] = Corpus[(i * 3) % Corpus.Length],
            })));

        return index;
    }

    private static ITextScorer Build(string scorerKind) =>
        scorerKind switch
        {
            "bm25" => new Bm25Scorer(),
            "bm25-tuned" => new Bm25Scorer(0.9, 0.4),
            "bm25k0" => new Bm25Scorer(0.0, 0.75),
            "bm25b0" => new Bm25Scorer(1.5, 0.0),
            "bm25b1" => new Bm25Scorer(1.2, 1.0),
            "tfidf" => new TfIdfScorer(),
            "ql" => new QueryLikelihoodScorer(),
            "ql-tuned" => new QueryLikelihoodScorer(0.7),
            "bm25f" => new Bm25FScorer(),
            "bm25f-weighted" => new Bm25FScorer(
                1.4, 0.6, new Dictionary<string, double> { ["title"] = 2.5 }),
            "bm25plus" => new Bm25PlusScorer(),
            "bm25plus-tuned" => new Bm25PlusScorer(1.1, 0.6, 0.9),
            "bm25l" => new Bm25LScorer(),
            "bm25l-tuned" => new Bm25LScorer(1.3, 0.5, 0.4),
            _ => throw new ArgumentOutOfRangeException(nameof(scorerKind), scorerKind, "unknown scorer"),
        };

    private static IEnumerable<string> DistinctQueries() =>
        ["term", "the quick", "every letter scoring", "zulu missing term"];

    /// <summary>
    /// The same list plus a repeated term. Both sides of the accumulation comparison below are
    /// <em>plans</em>, and plans do not deduplicate the way <see cref="ITextScorer.Score"/> does,
    /// so both count the repetition the same number of times and still have to agree exactly.
    /// The engine itself never hands a plan a duplicated list — it deduplicates once and wraps
    /// the result in a <c>DistinctTermList</c> — which is why the divergence does not reach it.
    /// </summary>
    private static IEnumerable<string> Queries() =>
        ["term", "the quick", "every letter scoring", "zulu missing term", "term term term"];
}
