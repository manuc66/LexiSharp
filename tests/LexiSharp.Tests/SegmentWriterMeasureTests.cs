using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The segment's length is measured before its first byte is written, so the two must agree exactly:
/// the writer asserts it on every write, and this asserts it over every shape of corpus it can be
/// asked to write.
/// </summary>
/// <remarks>
/// <para>
/// A disagreeing measure is not a wrong score — it is a truncated or overflowing segment, and it is
/// silent. The write itself refuses both directions, so the measure cannot corrupt a file, but it can
/// refuse a corpus it should have written. That is what this suite is for: shapes the hand-derived
/// sizes get wrong are shapes the property test finds.
/// </para>
/// <para>
/// The shapes that decide it are the ones where a byte count is easy to get wrong: UTF-8 of one to
/// four bytes per character, a term whose frequency or positions exceed a byte, dictionaries that are
/// absent, empty, and full of keys that sort differently from the order they were inserted in, and a
/// vocabulary whose terms differ only past the first character. A last character past 255 of positions
/// is what widens a block's positions plane to two bytes, and a measure that misses it is a byte short
/// per plane.
/// </para>
/// </remarks>
public class SegmentWriterMeasureTests
{
    [Fact]
    public void AnEmptyCorpusMeasuresToItsHeader()
    {
        var index = new InMemoryTextIndex();

        Assert.Equal(SegmentWriter.HeaderBytes, SegmentWriter.Measure(index));
        Assert.Equal(SegmentWriter.HeaderBytes, SegmentWriter.Write(index).Length);
    }

    [Fact]
    public void AnEmptyDocumentMeasuresToItsRecord()
    {
        var index = new InMemoryTextIndex();

        index.Add(new SearchDocument(string.Empty, string.Empty));

        Assert.Equal(SegmentWriter.Write(index).Length, SegmentWriter.Measure(index));
        Assert.True(SegmentWriter.Write(index).Length > SegmentWriter.HeaderBytes);
    }

    /// <summary>
    /// Every byte width a document can carry: one to four bytes per character, and the two orderings
    /// that differ — <see cref="Rune"/> order, which the writer's dictionary uses, and UTF-16 order,
    /// which it must not.
    /// </summary>
    [Fact]
    public void EveryCharacterWidthMeasuresToItself()
    {
        var texts = new[]
        {
            "a 𐐨 ｚ Ａ",
            "é ü ñ ÿ ﬀ",
            "日本語テキスト",
            "𐐨 𐑎 𐒅",
            "emoji 🎉 and 🧭 mixed",
            "\"quoted\" 'apostrophes' &amp; entities",
        };

        foreach (string text in texts)
        {
            var index = new InMemoryTextIndex(new LexiSharp.Linguistics.Tokenizer(
                new LexiSharp.Linguistics.TokenizerOptions { FoldDiacritics = false, KeepSingleCharTerms = true }));

            index.Add(new SearchDocument("doc", text));

            Assert.Equal(SegmentWriter.Write(index).Length, SegmentWriter.Measure(index));

            // And the same corpus twice is the same bytes: the entries are written in key order, so the
            // measure and the write see one order whichever order the document's maps were built in.
            Assert.Equal(SegmentWriter.Write(index), SegmentWriter.Write(index));
        }
    }

    /// <summary>A frequency or a position past a byte widens a plane, which is where a measure goes short.</summary>
    [Fact]
    public void APostingsPlaneThatWidensToTwoBytesIsMeasured()
    {
        var index = new InMemoryTextIndex();

        // 400 repetitions: a frequency past a byte and positions past 255, which is the case a
        // per-entry width of one byte gets wrong by exactly the number of entries it fails to widen.
        index.Add(new SearchDocument("long", Repeat("term", 400)));

        Assert.Equal(SegmentWriter.Write(index).Length, SegmentWriter.Measure(index));
        Assert.Equal(400, index.CorpusFrequency("term"));
    }

    /// <summary>
    /// Every shape a dictionary can take, including one where keys were inserted in an order their own
    /// sort does not preserve, and one that overwrote a key so its count is smaller than its writers.
    /// </summary>
    [Fact]
    public void EveryDictionaryShapeIsMeasured()
    {
        var shapes = new IReadOnlyDictionary<string, string>?[]
        {
            null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["only"] = "value" },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["zz"] = "1", ["aa"] = "2", ["mm"] = "3" },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["é"] = "ü", ["日"] = "本", ["𐐨"] = "𐑎" },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "first", ["a"] = "second" },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["key"] = new string('v', 500) },
        };

        foreach (var shape in shapes)
        {
            var index = new InMemoryTextIndex();
            index.Add(new SearchDocument("doc", "text", shape, shape is null ? null : "category", shape));

            Assert.Equal(SegmentWriter.Write(index).Length, SegmentWriter.Measure(index));
        }
    }

    /// <summary>
    /// Many corpora of shapes no list would have thought of, each written and measured twice.
    /// </summary>
    /// <remarks>
    /// The seeds are the test: the corpus is a pure function of the seed, so a failure is reproducible
    /// from the number the report carries.
    /// </remarks>
    [Fact]
    public void GeneratedCorporaMeasureToExactlyWhatTheyWrite()
    {
        for (int seed = 0; seed < 120; seed++)
        {
            var index = Corpus(seed);

            int measured = SegmentWriter.Measure(index);
            byte[] written = SegmentWriter.Write(index);

            // Re-measuring writes nothing and changes nothing.
            Assert.Equal(measured, SegmentWriter.Measure(index));
            Assert.Equal(measured, written.Length);
        }
    }

    /// <summary>
    /// The measured segment is the written segment, not a file of the right size: it serves the same
    /// page as the index it was written from.
    /// </summary>
    [Fact]
    public void AMeasuredSegmentSearchesLikeTheIndexItWasWrittenFrom()
    {
        string path = Path.Combine(
            Path.GetTempPath(), "lexisharp-measure-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".lxs");

        try
        {
            for (int seed = 0; seed < 6; seed++)
            {
                var index = Corpus(seed);

                SegmentFile.Write(index, path);

                var expected = new RankedTextSearchEngine(index, new Bm25Scorer());
                var actual = new RankedTextSearchEngine(SegmentFile.Open(path), new Bm25Scorer());

                foreach (string query in Queries(index))
                {
                    var page = actual.Search(query);
                    var wanted = expected.Search(query);

                    Assert.Equal(wanted.Count, page.Count);

                    for (int i = 0; i < wanted.Count; i++)
                    {
                        Assert.Equal(wanted[i].DocumentId, page[i].DocumentId);
                        Assert.Equal(
                            BitConverter.DoubleToInt64Bits(wanted[i].Score),
                            BitConverter.DoubleToInt64Bits(page[i].Score));
                    }
                }
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// A corpus that cannot be measured is refused rather than wrapped into a reader of garbage.
    /// </summary>
    [Fact]
    public void AnUnsupportedIndexIsRefusedByBothPasses()
    {
        // An index that answers the corpus questions but does not carry the two capabilities a segment
        // needs — the shape an index arrives in when it was built to be read and never to be written.
        var index = new CorpusOnly(new InMemoryTextIndex());
        index.Add(new SearchDocument("doc", "text"));

        Assert.Throws<NotSupportedException>(() => SegmentWriter.Measure(index));
        Assert.Throws<NotSupportedException>(() => SegmentWriter.Write(index));
    }

    /// <summary>Everything a segment is not given: the corpus surface, and none of what it writes from.</summary>
    private sealed class CorpusOnly(InMemoryTextIndex index) : ITextIndex
    {
        public IReadOnlyCollection<SearchDocument> Documents => index.Documents;
        public int Count => index.Count;
        public double AverageDocumentLength => index.AverageDocumentLength;
        public int VocabularySize => index.VocabularySize;
        public long CorpusTokenCount => index.CorpusTokenCount;
        public bool Contains(string documentId) => index.Contains(documentId);
        public IReadOnlyList<string> GetTerms(string documentId) => index.GetTerms(documentId);
        public IReadOnlyList<int> GetTermPositions(string documentId, string term) => index.GetTermPositions(documentId, term);
        public int DocumentFrequency(string term) => index.DocumentFrequency(term);
        public int CorpusFrequency(string term) => index.CorpusFrequency(term);
        public int TermFrequency(string documentId, string term) => index.TermFrequency(documentId, term);
        public int DocumentLength(string documentId) => index.DocumentLength(documentId);
        public bool TryGetDocument(string documentId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SearchDocument? document) =>
            index.TryGetDocument(documentId, out document);
        public IReadOnlyCollection<string> Fields => index.Fields;

        public void Index(IEnumerable<SearchDocument> documents) => index.Index(documents);
        public void Add(SearchDocument document) => index.Add(document);
        public bool Remove(string documentId) => index.Remove(documentId);
        public void Clear() => index.Clear();
    }

    /// <summary>A corpus with a gap in it is refused by the measure, not only by the write.</summary>
    [Fact]
    public void ACorpusWithAGapIsRefusedByTheMeasure()
    {
        var index = new InMemoryTextIndex();

        index.Index(new[] { new SearchDocument("1", "one two"), new SearchDocument("2", "three four") });
        Assert.True(index.Remove("1"));

        Assert.Throws<NotSupportedException>(() => SegmentWriter.Measure(index));
        Assert.Throws<NotSupportedException>(() => SegmentWriter.Write(index));
    }

    private static string Repeat(string term, int count) =>
        string.Join(" ", Enumerable.Repeat(term, count));

    /// <summary>The queries that reach every document of the corpus, and one that reaches none.</summary>
    private static IEnumerable<string> Queries(ITextIndex index)
    {
        var queries = new List<string>();

        foreach (string term in ((IVocabularyIndex)index).Vocabulary)
            queries.Add(term);

        queries.Add("term text missing");
        queries.Add(string.Empty);

        return queries;
    }

    /// <summary>
    /// A corpus from a seed: the document count, the vocabulary's alphabets, every document's tokens,
    /// its metadata maps and its category.
    /// </summary>
    private static InMemoryTextIndex Corpus(int seed)
    {
        var random = new Random(seed);
        var index = new InMemoryTextIndex(new LexiSharp.Linguistics.Tokenizer(
            new LexiSharp.Linguistics.TokenizerOptions { FoldDiacritics = false, KeepSingleCharTerms = true }));
        var words = Vocabulary(random.Next(1, 9));
        int documents = random.Next(0, 10);

        for (int i = 0; i < documents; i++)
        {
            var text = new StringBuilder();
            int tokens = random.Next(0, 400);

            for (int t = 0; t < tokens; t++)
            {
                if (t > 0 && random.Next(6) == 0)
                    text.Append(" … ");

                text.Append(words[random.Next(words.Length)]).Append(' ');
            }

            index.Add(new SearchDocument(
                Id(random, i),
                text.ToString(),
                Map(random, "value"),
                random.Next(4) == 0 ? Category(random) : null,
                Map(random, "text")));
        }

        return index;
    }

    private static string Id(Random random, int ordinal)
    {
        var id = new StringBuilder("doc-");
        id.Append(ordinal.ToString("D3", CultureInfo.InvariantCulture));

        if (random.Next(5) == 0)
            id.Insert(0, Decorate(random));

        return id.ToString();
    }

    private static string Decorate(Random random) =>
        random.Next(3) switch
        {
            0 => "é",
            1 => "日",
            _ => "𐐨",
        };

    private static string Category(Random random) =>
        random.Next(2) == 0 ? "category" : "catégorie " + Decorate(random);

    /// <summary>A metadata map, including the shapes that sort differently from how they were built.</summary>
    private static Dictionary<string, string>? Map(Random random, string value)
    {
        if (random.Next(4) == 0)
            return null;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        int entries = random.Next(0, 6);

        for (int i = 0; i < entries; i++)
        {
            string key = Key(random);

            // An overwrite rather than an insertion, so the count and the entries can disagree unless
            // both passes read the same map.
            map[key] = random.Next(3) == 0 ? value + i.ToString(CultureInfo.InvariantCulture) + Decorate(random) : value;
        }

        return map;
    }

    private static string Key(Random random) =>
        random.Next(5) switch
        {
            0 => "zulu",
            1 => "alpha",
            2 => "éclair",
            3 => "日本",
            _ => "a",
        };

    /// <summary>A vocabulary whose alphabets cover every UTF-8 byte width, and whose terms differ by their first character.</summary>
    private static string[] Vocabulary(int count)
    {
        var alphabets = new[]
        {
            "abcde",
            "éüñÿ",
            "日本語テキスト",
            "aé日",
        };

        var words = new List<string>(count + 2);

        for (int i = 0; i < count; i++)
        {
            string alphabet = alphabets[i % alphabets.Length];
            int length = 1 + (i % 2);
            var word = new StringBuilder(length);

            for (int c = 0; c < length; c++)
                word.Append(alphabet[(i + c) % alphabet.Length]);

            words.Add(word.ToString());
        }

        // Terms outside the BMP, carried whole: a code point order and a UTF-16 order disagree on
        // where these belong, and the writer's dictionary is the code point one.
        words.Add("𐐨");
        words.Add("𐑎");

        return [.. words];
    }
}
