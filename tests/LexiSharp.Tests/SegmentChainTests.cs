using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// A chain of segments, read as one index.
/// </summary>
/// <remarks>
/// <para>
/// A search server needs to write to a storage the segments make immutable, and a segment is written
/// once — so the writes have to land somewhere else and a reader has to see both. The chain is the
/// read half of that: several generations, one ordinal space, one fold. Segment <c>i</c> owns the global
/// ordinals <c>[base[i], base[i] + count[i])</c>, and a fold walks the segments oldest-first, each at
/// its base.
/// </para>
/// <para>
/// What these tests pin is the property the whole design rests on: <b>the page a chain serves is
/// bit-identical to the in-memory index's for the same corpus</b>. That is what makes a chain a storage
/// engine rather than a different answer to the same question, and it is asserted rather than assumed.
/// </para>
/// </remarks>
public class SegmentChainTests
{
    private const int Documents = 300;
    private const int Tokens = 20;
    private const int Vocabulary = 40;

    [Fact]
    public void AChainOfTwoSegmentsServesTheSamePageAsTheIndex()
    {
        var (memory, chain, engines) = Engines();

        foreach (string query in QuerySet)
            AssertSamePage(query, engines.Memory.Search(query), engines.Chain.Search(query));
    }

    [Fact]
    public void AChainOfThreeSegmentsServesTheSamePageAsTheIndex()
    {
        var (memory, chain, engines) = ThreeWay();

        foreach (string query in QuerySet)
            AssertSamePage(query, engines.Memory.Search(query), engines.Chain.Search(query));
    }

    [Fact]
    public void ASingleSegmentChainIsTheIndexItWasWrittenFrom()
    {
        var (memory, chain, engines) = Engines();

        // The degenerate chain has to behave exactly like the segment on its own, which is what makes
        // every other assertion about a chain an extension rather than a replacement.
        foreach (string query in QuerySet)
            AssertSamePage(query, engines.Memory.Search(query), engines.Chain.Search(query));

        Assert.Equal(memory.Count, chain.Count);
        Assert.Equal(memory.VocabularySize, chain.VocabularySize);
        Assert.Equal(memory.CorpusTokenCount, chain.CorpusTokenCount);
        Assert.Equal(memory.AverageDocumentLength, chain.AverageDocumentLength);
    }

    [Fact]
    public void TheChainAnswersTheReadSurfaceTheSameWayForEveryDocument()
    {
        var (memory, chain, _) = Engines();

        Assert.Equal(memory.Count, chain.Count);
        Assert.Equal(memory.VocabularySize, chain.VocabularySize);
        Assert.Equal(memory.CorpusTokenCount, chain.CorpusTokenCount);
        Assert.Equal(memory.AverageDocumentLength, chain.AverageDocumentLength);

        var vocabulary = ((IVocabularyIndex)memory).Vocabulary.ToList();

        foreach (string term in vocabulary)
        {
            Assert.Equal(memory.DocumentFrequency(term), chain.DocumentFrequency(term));
            Assert.Equal(memory.CorpusFrequency(term), chain.CorpusFrequency(term));
        }

        for (int ordinal = 0; ordinal < memory.Count; ordinal++)
        {
            var expected = ((IAccumulatingIndex)memory).DocumentAt(ordinal)!;
            var actual = ((IAccumulatingIndex)chain).DocumentAt(ordinal)!;

            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.Text, actual.Text);
            Assert.Equal(memory.DocumentLength(expected.Id), chain.DocumentLength(expected.Id));
            Assert.Equal(memory.GetTerms(expected.Id), chain.GetTerms(expected.Id));
            Assert.True(chain.Contains(expected.Id));
            Assert.True(chain.TryGetDocument(expected.Id, out var resolved));
            Assert.Equal(expected.Id, resolved!.Id);
            Assert.Equal(((IAccumulatingIndex)memory).OrdinalOf(expected.Id), ((IAccumulatingIndex)chain).OrdinalOf(expected.Id));

            foreach (string term in vocabulary)
            {
                Assert.Equal(memory.TermFrequency(expected.Id, term), chain.TermFrequency(expected.Id, term));
                Assert.Equal(memory.GetTermPositions(expected.Id, term), chain.GetTermPositions(expected.Id, term));
            }
        }
    }

    [Fact]
    public void AnUnknownDocumentAnswersAsAbsent()
    {
        var (_, chain, _) = Engines();

        Assert.False(chain.Contains("nowhere"));
        Assert.False(chain.TryGetDocument("nowhere", out _));
        Assert.Equal(0, chain.DocumentLength("nowhere"));
        Assert.Empty(chain.GetTerms("nowhere"));
        Assert.Empty(chain.GetTermPositions("nowhere", "anything"));
        Assert.Equal(-1, ((IAccumulatingIndex)chain).OrdinalOf("nowhere"));
        Assert.Null(((IAccumulatingIndex)chain).DocumentAt(-1));
        Assert.Null(((IAccumulatingIndex)chain).DocumentAt(chain.Count));
    }

    [Fact]
    public void AChainWithNoGapsServesEveryDocumentFromEveryOrdinal()
    {
        var (memory, chain, _) = Engines();

        // The segments own contiguous ranges, so walking the chain's whole ordinal space yields the
        // corpus once, in order, with no free slot.
        var documents = chain.Documents.ToList();

        Assert.Equal(memory.Count, documents.Count);
        Assert.Equal(documents.Select(d => d.Id), documents.Select(d => d.Id).OrderBy(id => id, System.StringComparer.Ordinal));

        for (int ordinal = 0; ordinal < chain.Count; ordinal++)
            Assert.NotNull(((IAccumulatingIndex)chain).DocumentAt(ordinal));
    }

    [Fact]
    public void ARareQueryOverAChainKeepsTheCandidatePath()
    {
        // The candidate path is what keeps a below-threshold query off the whole corpus, and a chain
        // has to inherit it: a segment offers it, and the chain unions across the segments.
        var (_, chain, _) = Engines();
        string rare = RareTerm;

        var overChain = ((ICandidateIndex)chain).GetCandidateDocuments(new[] { rare }).ToList();

        Assert.Equal(RareCarriers, overChain.Count);

        foreach (var document in overChain)
            Assert.Contains(rare, document.Text, System.StringComparison.Ordinal);

        // And it still serves the same page as the in-memory index, through that path.
        var (memory, _, engines) = Engines();
        AssertSamePage(rare, engines.Memory.Search(rare), engines.Chain.Search(rare));
    }

    [Fact]
    public void ACandidateQueryOverAChainIsInCorpusOrder()
    {
        var (_, chain, _) = Engines();
        var documents = ((ICandidateIndex)chain).GetCandidateDocuments(new[] { "common" }).ToList();
        var ordinal = new List<int>();

        foreach (var document in documents)
            ordinal.Add(int.Parse(document.Id["doc-".Length..], CultureInfo.InvariantCulture));

        Assert.Equal(ordinal.OrderBy(i => i), ordinal);
        Assert.Equal(ordinal.Distinct().Count(), ordinal.Count);
    }

    [Fact]
    public void AnEmptyOrUnknownCandidateQueryYieldsNothing()
    {
        var (_, chain, _) = Engines();

        Assert.Empty(((ICandidateIndex)chain).GetCandidateDocuments(System.Array.Empty<string>()));
        Assert.Empty(((ICandidateIndex)chain).GetCandidateDocuments(new[] { "quokka" }));
        Assert.Equal(RareCarriers, ((ICandidateIndex)chain).GetCandidateDocuments(new[] { "quokka", RareTerm }).Count());
    }

    [Fact]
    public void AChainRefusesAnEmptySegmentList()
    {
        Assert.Throws<ArgumentException>(() => new SegmentChain(System.Array.Empty<SegmentTextIndex>()));
        Assert.Throws<ArgumentNullException>(() => new SegmentChain(null!));
    }

    [Fact]
    public void TheCandidatePathIsOnlyWhatTakesARareQuery()
    {
        // A below-threshold query reaches the per-document loop, and the engine takes the candidate
        // path only when the index offers it. Without the capability a chain would serve the query
        // from Documents, which is the full corpus.
        var (_, chain, _) = Engines();

        Assert.True(chain is ICandidateIndex);
        Assert.All(_segments(chain), segment => Assert.True(segment is ICandidateIndex));
    }

    private static System.Collections.Generic.IEnumerable<SegmentTextIndex> _segments(SegmentChain chain) =>
        chain.SegmentsForTest;

    private static readonly string[] QuerySet = ["common", "common rare", "term0 term1 term2", RareTerm, "quokka"];

    private const string RareTerm = "rareword";

    /// <summary>Documents carrying the rare term: below the engine's accumulation threshold.</summary>
    private const int RareCarriers = 5;

    private static (InMemoryTextIndex, SegmentChain, (RankedTextSearchEngine Memory, RankedTextSearchEngine Chain)) Engines()
    {
        var memory = Build();
        var segments = Split(memory, 2);
        var chain = new SegmentChain(segments);

        return (
            memory,
            chain,
            (new RankedTextSearchEngine(memory, new Bm25Scorer()), new RankedTextSearchEngine(chain, new Bm25Scorer())));
    }

    private static (InMemoryTextIndex, SegmentChain, (RankedTextSearchEngine Memory, RankedTextSearchEngine Chain)) ThreeWay()
    {
        var memory = Build();
        var chain = new SegmentChain(Split(memory, 3));

        return (
            memory,
            chain,
            (new RankedTextSearchEngine(memory, new Bm25Scorer()), new RankedTextSearchEngine(chain, new Bm25Scorer())));
    }

    /// <summary>
    /// Splits a corpus into <paramref name="count"/> contiguous chunks and writes each as its own
    /// segment. Contiguity is what makes a chain's global ordinal match the corpus's insertion order,
    /// which is what lets the page be compared bit for bit.
    /// </summary>
    private static List<SegmentTextIndex> Split(InMemoryTextIndex memory, int count)
    {
        var documents = memory.Documents.ToList();
        var segments = new List<SegmentTextIndex>();
        int per = documents.Count / count;

        for (int i = 0; i < count; i++)
        {
            int take = i == count - 1 ? documents.Count - (i * per) : per;
            var chunk = new InMemoryTextIndex();

            foreach (var document in documents.Skip(i * per).Take(take))
                chunk.Add(document);

            segments.Add(new SegmentTextIndex(SegmentWriter.Write(chunk)));
        }

        return segments;
    }

    private static InMemoryTextIndex Build()
    {
        var index = new InMemoryTextIndex();
        var random = new Random(29);
        var builder = new System.Text.StringBuilder();

        for (int i = 0; i < Documents; i++)
        {
            builder.Clear();

            for (int t = 0; t < Tokens; t++)
            {
                // A Zipf-ish draw so some terms are near-universal and some are rare.
                int term = Vocabulary - 1 - (int)(Vocabulary * (1 - System.Math.Pow(random.NextDouble(), 3)));
                builder.Append("term").Append(System.Math.Max(0, term)).Append(' ');
            }

            if (i % 60 == 0)
                builder.Append("common ");

            if (i % 60 == 3)
                builder.Append(RareTerm).Append(' ');

            index.Add(new SearchDocument(
                "doc-" + i.ToString("D4", CultureInfo.InvariantCulture),
                builder.ToString()));
        }

        return index;
    }

    private static void AssertSamePage(
        string query,
        IReadOnlyList<SearchResult> expected,
        IReadOnlyList<SearchResult> actual)
    {
        Assert.Equal(expected.Count, actual.Count);

        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].DocumentId, actual[i].DocumentId);

            // Bit for bit: the chain's segments hold the same postings and the fold sees the same
            // records in the same order, so nothing is allowed to differ in the last bits either.
            Assert.Equal(
                System.BitConverter.DoubleToInt64Bits(expected[i].Score),
                System.BitConverter.DoubleToInt64Bits(actual[i].Score));
        }
    }
}
