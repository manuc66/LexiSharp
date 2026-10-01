using Xunit;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;

namespace LexiSharp.Tests;

/// <summary>
/// The four options that make BM25 reproduce a recorded score rather than merely reproduce a ranking:
/// the document count the idf is built from, the absence of the saturation constant, the precision of
/// the arithmetic, and the rounding of what is returned.
/// </summary>
/// <remarks>
/// Each is measured here against the arithmetic it claims, not against a stored constant. A stored
/// constant would only prove the code has not changed, which is what the pins elsewhere in this suite
/// are for; what is worth pinning here is that each option does what its name says, because that is the
/// part a reader of a run file has to be able to trust.
/// </remarks>
public class Bm25ReproductionTests
{
    private static InMemoryTextIndex Index(out ITokenizer tokenizer, AverageLengthDivisor divisor = AverageLengthDivisor.AllDocuments)
    {
        tokenizer = new Tokenizer(new TokenizerOptions { Stemmer = new PorterStemmer() });

        var index = new InMemoryTextIndex(tokenizer, divisor);
        index.Index(
        [
            // One empty document: the difference between the two document counts, which is the whole
            // point of the first option and the reason a test corpus needs one.
            new SearchDocument("empty", ""),
            new SearchDocument("a", "alpha alpha beta gamma"),
            new SearchDocument("b", "alpha beta"),
            new SearchDocument("c", "gamma gamma delta epsilon zeta"),
        ]);

        return index;
    }

    [Fact]
    public void Idf_Is_Built_From_The_Count_The_Length_Average_Divides_By()
    {
        var index = Index(out var tokenizer, AverageLengthDivisor.NonEmptyDocuments);
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer(1.2, 0.75), tokenizer);

        double actual = engine.Search("alpha", new SearchOptions(10))[0].Score;

        // The idf's N is the count the average length divides by. Three of the four documents hold a
        // term, and under this divisor the average is over those three, so N is three and not four.
        const int n = 3;
        int df = index.DocumentFrequency("alpha");
        double idf = Math.Log(1.0 + (n - df + 0.5) / (df + 0.5));

        double expected = idf * 2 * 2.2 / (2 + 1.2 * (1.0 - 0.75 + 0.75 * 4 / index.AverageDocumentLength));

        Assert.Equal(expected, actual, 12);
    }

    [Fact]
    public void Default_Idf_Still_Uses_Every_Indexed_Document()
    {
        var index = Index(out var tokenizer);
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer(1.2, 0.75), tokenizer);

        double actual = engine.Search("alpha", new SearchOptions(10))[0].Score;

        // Four documents are indexed, one of them empty, and the default counts all four. This is the
        // behaviour the other option changes, so it is asserted rather than assumed.
        const int n = 4;
        int df = index.DocumentFrequency("alpha");
        double idf = Math.Log(1.0 + (n - df + 0.5) / (df + 0.5));

        double expected = idf * 2 * 2.2 / (2 + 1.2 * (1.0 - 0.75 + 0.75 * 4 / index.AverageDocumentLength));

        Assert.Equal(expected, actual, 12);
    }

    [Fact]
    public void StatisticDocumentCount_Follows_The_Length_Divisor()
    {
        var all = Index(out _, AverageLengthDivisor.AllDocuments);
        var nonEmpty = Index(out _, AverageLengthDivisor.NonEmptyDocuments);

        Assert.Equal(4, all.StatisticDocumentCount);
        Assert.Equal(3, nonEmpty.StatisticDocumentCount);
        Assert.Equal(all.Count, all.StatisticDocumentCount);
    }

    [Fact]
    public void Omitting_The_Saturation_Constant_Scales_Every_Score_By_One_Over_K1PlusOne()
    {
        var index = Index(out var tokenizer);
        var withConstant = new RankedTextSearchEngine(index, new Bm25Scorer(0.9, 0.4), tokenizer);
        var without = new RankedTextSearchEngine(index, new Bm25Scorer(0.9, 0.4, saturationConstant: false), tokenizer);

        var left = withConstant.Search("alpha", new SearchOptions(10));
        var right = without.Search("alpha", new SearchOptions(10));

        Assert.Equal(left.Count, right.Count);

        for (int i = 0; i < left.Count; i++)
        {
            Assert.Equal(left[i].DocumentId, right[i].DocumentId);
            Assert.Equal(left[i].Score / 1.9, right[i].Score, 12);
        }
    }

    [Fact]
    public void Single_Precision_Narrows_Every_Per_Term_Contribution()
    {
        var index = Index(out var tokenizer);
        var wide = new RankedTextSearchEngine(index, new Bm25Scorer(0.9, 0.4), tokenizer);
        var narrow = new RankedTextSearchEngine(
            index, new Bm25Scorer(0.9, 0.4, arithmetic: Bm25Arithmetic.SinglePrecision), tokenizer);

        var wideScores = wide.Search("alpha", new SearchOptions(10)).Select(r => r.Score).ToArray();
        var narrowScores = narrow.Search("alpha", new SearchOptions(10)).Select(r => r.Score).ToArray();

        // The total is narrowed once, so every score is a single-precision value and differs from the
        // double one by less than half a unit in the last place.
        foreach (double score in narrowScores)
            Assert.Equal((float)score, score);

        for (int i = 0; i < wideScores.Length; i++)
            Assert.True(
                Math.Abs(wideScores[i] - narrowScores[i]) <= 1e-6,
                $"score {i}: {wideScores[i]} contre {narrowScores[i]}");
    }

    [Fact]
    public void Four_Decimal_Rounding_Moves_Every_Score_By_Less_Than_A_Ten_Thousandth()
    {
        var index = Index(out var tokenizer);
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer(0.9, 0.4), tokenizer);

        var plain = engine.Search("alpha", new SearchOptions(10));
        var rounded = engine.Search("alpha", new SearchOptions(10).WithScoreRounding(ScoreRounding.FourDecimals));

        Assert.Equal(plain.Count, rounded.Count);

        for (int i = 0; i < rounded.Count; i++)
        {
            Assert.Equal(plain[i].DocumentId, rounded[i].DocumentId);

            // A ten-thousandth of rounding, plus the walk a tied group can add on top of it: a score
            // that lands within that of another is moved down by a millionth per position, and the most
            // a small page can walk is one per hit.
            Assert.True(
                Math.Abs(plain[i].Score - rounded[i].Score) <= 1e-4 + rounded.Count * 1e-6,
                $"{rounded[i].DocumentId}: {plain[i].Score} contre {rounded[i].Score}");

            // And every returned score is a single-precision value, because that is what is stored.
            Assert.Equal((float)rounded[i].Score, rounded[i].Score);
        }
    }

    [Fact]
    public void Four_Decimal_Rounding_Leaves_The_Order_Alone()
    {
        var index = Index(out var tokenizer);
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer(0.9, 0.4), tokenizer);

        var plain = engine.Search("gamma", new SearchOptions(10)).Select(r => r.DocumentId).ToArray();
        var rounded = engine
            .Search("gamma", new SearchOptions(10).WithScoreRounding(ScoreRounding.FourDecimals))
            .Select(r => r.DocumentId)
            .ToArray();

        // It merges scores that differ by less than half a ten-thousandth and then separates them again
        // by rank, so the ranking it returns is the ranking it was given.
        Assert.Equal(plain, rounded);
    }

    [Fact]
    public void A_Tied_Group_Is_Walked_Down_By_Rank()
    {
        // Three documents with identical text score identically, which is the only case the tie walk
        // exists for.
        var tokenizer = new Tokenizer(new TokenizerOptions { Stemmer = new PorterStemmer() });
        var index = new InMemoryTextIndex(tokenizer);
        index.Index(
        [
            new SearchDocument("one", "alpha"),
            new SearchDocument("two", "alpha"),
            new SearchDocument("three", "alpha"),
        ]);

        var engine = new RankedTextSearchEngine(index, new Bm25Scorer(0.9, 0.4), tokenizer);
        var results = engine.Search("alpha", new SearchOptions(10).WithScoreRounding(ScoreRounding.FourDecimals));

        // Three identical documents score identically, so the walk is what separates them: the first
        // keeps the rounded score and each next one drops by a further millionth.
        Assert.Equal(3, results.Count);
        Assert.True(results[0].Score > results[1].Score, $"{results[0].Score} contre {results[1].Score}");
        Assert.True(results[1].Score > results[2].Score, $"{results[1].Score} contre {results[2].Score}");
        Assert.Equal(1e-6, results[0].Score - results[1].Score, 6);
        Assert.Equal(1e-6, results[1].Score - results[2].Score, 6);
    }

    [Fact]
    public async Task Single_Precision_Gives_The_Same_Answer_Under_Concurrency()
    {
        // The reciprocal table is built on first use and cached on the scorer, and one scorer serves every
        // query an engine runs. It is therefore read and written from several threads at once, which is
        // the one place this mode has state: a table published before it is filled would be read while
        // it is being written, and the symptom would be a score that is wrong occasionally rather than a
        // failure. Run it serially first, then on many threads at once, and require the two to agree
        // exactly — the comparison is on the raw bits because a half-written table would not be wrong by
        // a rounding, it would be wrong by an arbitrary amount.
        var index = Index(out var tokenizer);
        var engine = new RankedTextSearchEngine(
            index,
            new Bm25Scorer(0.9, 0.4, saturationConstant: false, arithmetic: Bm25Arithmetic.SinglePrecision),
            tokenizer);

        string[] queries = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta"];
        var serial = queries.ToDictionary(q => q, q => engine.Search(q, new SearchOptions(10)));

        var engineShared = new RankedTextSearchEngine(
            index,
            new Bm25Scorer(0.9, 0.4, saturationConstant: false, arithmetic: Bm25Arithmetic.SinglePrecision),
            tokenizer);

        var results = new System.Collections.Concurrent.ConcurrentDictionary<
            string, System.Collections.Concurrent.ConcurrentBag<IReadOnlyList<SearchResult>>>();

        await Task.WhenAll(Enumerable.Range(0, 64).Select(iteration => Task.Run(() =>
        {
            // A fresh scorer per iteration would not exercise anything: the point is that all 64 share
            // one, and the table is published on whichever of them gets there first.
            string query = queries[iteration % queries.Length];
            results.GetOrAdd(query, _ => new()).Add(engineShared.Search(query, new SearchOptions(10)));
        })));

        foreach (var query in queries)
        {
            var expected = serial[query];

            foreach (var actual in results[query])
            {
                Assert.Equal(expected.Count, actual.Count);

                for (int i = 0; i < expected.Count; i++)
                {
                    Assert.Equal(expected[i].DocumentId, actual[i].DocumentId);
                    Assert.Equal(expected[i].Score, actual[i].Score);
                }
            }
        }
    }

    /// <summary>
    /// Every parameter spelled out, and the result identical to <see cref="SearchOptions.Default"/>.
    /// Nine of them, positionally: the tenth — the write-down rounding — is no longer a parameter,
    /// because it is internal and reached through a method, and a parameter nobody outside the
    /// harness should pass is not one this test should be asserting the default of.
    /// </summary>
    [Fact]
    public void The_Defaults_Change_Nothing()
    {
        var index = Index(out var tokenizer);
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer(), tokenizer);

        var defaults = engine.Search("alpha", SearchOptions.Default);
        var spelledOut = engine.Search("alpha", new SearchOptions(
            10,
            double.NegativeInfinity,
            null,
            0,
            false,
            null,
            null,
            true,
            TieBreak.DocumentId));

        Assert.Equal(defaults.Count, spelledOut.Count);

        for (int i = 0; i < defaults.Count; i++)
        {
            Assert.Equal(defaults[i].DocumentId, spelledOut[i].DocumentId);
            Assert.Equal(defaults[i].Score, spelledOut[i].Score);
        }
    }
}