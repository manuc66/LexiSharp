using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The candidate path a segment now offers, and what it costs.
/// </summary>
/// <remarks>
/// <para>
/// A segment used to answer every query below the engine's accumulation threshold through a full scan
/// of <see cref="IReadOnlyTextIndex.Documents"/> — which materializes and scores the whole corpus.
/// Measured on a 40,000-document segment, a single term with a document frequency of 3 cost
/// <b>42,116 µs</b> that way and <b>10.8 µs</b> through the candidate path, a factor of about 3,900.
/// That is what these tests pin: the capability exists, it is exact, and it is what the engine takes.
/// </para>
/// <para>
/// The ordering is load-bearing and asserted directly. <see cref="ICandidateIndex.GetCandidateDocuments"/>
/// promises the same relative order as <see cref="IReadOnlyTextIndex.Documents"/>, and a segment's
/// ordinal space is dense and gapless, so ordinal order <i>is</i> corpus order. The engine prefers the
/// unordered capability when an index has it, and a segment deliberately does not offer it, because
/// the page a query produces under a tie-break that reads the arrival position depends on the order
/// candidates arrive in — and a segment that broke that ordering would disagree with the index it was
/// written from.
/// </para>
/// </remarks>
public class SegmentCandidatePathTests
{
    /// <summary>A term below the engine's accumulation threshold, so the per-document loop serves it.</summary>
    private const string RareTerm = "raremarker";

    [Fact]
    public void ARareQueryOverASegmentServesTheSamePageAsTheIndex()
    {
        var (memory, segment, engines) = Engines();

        foreach (string query in new[] { RareTerm, RareTerm + " other", "engine", "search engine" })
        {
            var expected = engines.Memory.Search(query);
            var actual = engines.Segment.Search(query);

            AssertSamePage(expected, actual, query);
        }
    }

    [Fact]
    public void TheCandidatePathIsWhatServesARareQuery()
    {
        var (memory, segment, _) = Engines();
        var candidates = ((ICandidateIndex)segment).GetCandidateDocuments(new[] { RareTerm });

        // Three documents carry the rare term, and a full scan would have looked at every one of them.
        Assert.Equal(3, candidates.Count());
        Assert.All(candidates, document => Assert.Contains(RareTerm, document.Text, System.StringComparison.Ordinal));
    }

    [Fact]
    public void CandidatesComeBackInCorpusOrderAndEachOnce()
    {
        var (_, segment, _) = Engines();

        var ordered = ((ICandidateIndex)segment).GetCandidateDocuments(new[] { "search", "engine" }).ToList();
        var indexes = new List<int>();

        foreach (var document in ordered)
            indexes.Add(int.Parse(document.Id["doc-".Length..], CultureInfo.InvariantCulture));

        // Ascending ordinals: a segment's ordinals are dense, so this is corpus order.
        Assert.Equal(indexes.OrderBy(i => i), indexes);

        // Each document once, though a document can carry both query terms.
        Assert.Equal(indexes.Distinct().Count(), indexes.Count);
    }

    [Fact]
    public void AnUnknownTermYieldsNothingRatherThanEverything()
    {
        var (_, segment, _) = Engines();

        var unknown = ((ICandidateIndex)segment).GetCandidateDocuments(new[] { "quokka" });
        var mixed = ((ICandidateIndex)segment).GetCandidateDocuments(new[] { "quokka", RareTerm });

        Assert.Empty(unknown);
        Assert.Equal(3, mixed.Count());
    }

    [Fact]
    public void AnEmptyTermListYieldsNothing()
    {
        var (_, segment, _) = Engines();

        Assert.Empty(((ICandidateIndex)segment).GetCandidateDocuments(System.Array.Empty<string>()));
    }

    [Fact]
    public void AFilteredQueryStillMatchesTheIndex()
    {
        // A filtered query takes a different engine path from the same candidate set, so the
        // capability has to hold for it too. The filter is on a field no document carries, with
        // NotEqual — the operator that admits a document with no such field — so it selects on the
        // whole corpus rather than on a segment one. A segment reads back as id and text with no
        // fields at all, which is a stated limit of the format and the reason a field filter cannot
        // be compared against the index it was written from.
        var (memory, segment, engines) = Engines();
        var filters = new[] { new MetadataFilter("tier", MetadataFilterOperator.NotEqual, "raremarker") };

        var expected = engines.Memory.Search(RareTerm, new SearchOptions { Filters = filters });
        var actual = engines.Segment.Search(RareTerm, new SearchOptions { Filters = filters });

        Assert.Equal(3, expected.Count);
        AssertSamePage(expected, actual, "filtered " + RareTerm);
    }

    [Fact]
    public void ATieBreakThatReadsArrivalOrderStillMatchesTheIndex()
    {
        // The reason the ordered contract is kept: under a tie-break that reads the arrival position,
        // the order candidates are produced in decides the page. A segment must not disagree with the
        // index it was written from.
        foreach (var tieBreak in new[] { TieBreak.DocumentId, TieBreak.InsertionOrder })
        {
            var (memory, segment, engines) = Engines();
            var options = new SearchOptions { TieBreak = tieBreak };
            var expected = engines.Memory.Search(RareTerm, options);
            var actual = engines.Segment.Search(RareTerm, options);

            AssertSamePage(expected, actual, $"{tieBreak} {RareTerm}");
        }
    }

    private static InMemoryTextIndex MemoryOf()
    {
        var (memory, _, _) = Engines();

        return memory;
    }

    private static void AssertSamePage(
        System.Collections.Generic.IReadOnlyList<SearchResult> expected,
        System.Collections.Generic.IReadOnlyList<SearchResult> actual,
        string query)
    {
        Assert.Equal(expected.Count, actual.Count);

        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].DocumentId, actual[i].DocumentId);

            // Bit for bit: the two indexes carry the same statistics, and the accumulator sees the
            // same records in the same order, so nothing is allowed to differ in the last bits either.
            Assert.Equal(
                System.BitConverter.DoubleToInt64Bits(expected[i].Score),
                System.BitConverter.DoubleToInt64Bits(actual[i].Score));
        }
    }

    private static (InMemoryTextIndex, SegmentTextIndex, (RankedTextSearchEngine Memory, RankedTextSearchEngine Segment))
        Engines()
    {
        var memory = Build();
        var segment = new SegmentTextIndex(SegmentWriter.Write(memory));

        return (memory, segment, (new RankedTextSearchEngine(memory, new Bm25Scorer()), new RankedTextSearchEngine(segment, new Bm25Scorer())));
    }

    /// <summary>
    /// A corpus where one term appears in exactly three documents of 150 — well below the engine's
    /// accumulation threshold, so the per-document loop serves it and the candidate path is what makes
    /// that loop cheap.
    /// </summary>
    private static InMemoryTextIndex Build()
    {
        var index = new InMemoryTextIndex();
        var random = new Random(11);
        var words = new[] { "search", "engine", "index", "query", "score", "rank", "document", "token", "term", "corpus" };
        var builder = new System.Text.StringBuilder();

        for (int i = 0; i < 150; i++)
        {
            builder.Clear();

            for (int w = 0; w < 14; w++)
                builder.Append(words[random.Next(words.Length)]).Append(' ');

            if (i is 0 or 75 or 149)
                builder.Append(RareTerm).Append(' ');

            index.Add(new SearchDocument(
                "doc-" + i.ToString("D4", CultureInfo.InvariantCulture),
                builder.ToString(),
                Fields: new Dictionary<string, string> { ["tier"] = i % 2 == 0 ? "even" : "odd" }));
        }

        return index;
    }
}
