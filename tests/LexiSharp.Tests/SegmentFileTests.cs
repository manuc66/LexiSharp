using System;
using System.Globalization;
using System.IO;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// A segment written to disk and reopened later is the corpus it was written from: the same page, bit
/// for bit, without the tokenizer running again.
/// </summary>
public class SegmentFileTests
{
    [Fact]
    public void ASegmentOnDiskSearchesLikeTheIndexItWasWrittenFrom()
    {
        var index = Build();
        var memory = new RankedTextSearchEngine(index, new Bm25Scorer());
        string path = Path.Combine(Path.GetTempPath(), "lexisharp-segment-" + Guid.NewGuid().ToString("N") + ".lxs");

        try
        {
            SegmentFile.Write(index, path);

            // The file is the bytes a writer produces, and nothing else.
            Assert.Equal(SegmentWriter.Write(index), File.ReadAllBytes(path));

            var reopened = new RankedTextSearchEngine(SegmentFile.Open(path), new Bm25Scorer());

            foreach (string query in new[] { "search", "search engine", "index query score", "quokka" })
            {
                var expected = memory.Search(query);
                var actual = reopened.Search(query);

                Assert.Equal(expected.Count, actual.Count);

                for (int i = 0; i < expected.Count; i++)
                {
                    Assert.Equal(expected[i].DocumentId, actual[i].DocumentId);
                    Assert.Equal(
                        BitConverter.DoubleToInt64Bits(expected[i].Score),
                        BitConverter.DoubleToInt64Bits(actual[i].Score));
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AFileThatIsNotASegmentIsRefused()
    {
        string path = Path.Combine(Path.GetTempPath(), "lexisharp-not-a-segment-" + Guid.NewGuid().ToString("N") + ".lxs");

        try
        {
            File.WriteAllText(path, "this is not a segment");

            Assert.Throws<InvalidDataException>(() => SegmentFile.Open(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static InMemoryTextIndex Build()
    {
        var index = new InMemoryTextIndex();
        var random = new Random(13);
        var words = new[] { "search", "engine", "index", "query", "score", "rank", "document", "token" };
        var builder = new System.Text.StringBuilder();

        for (int i = 0; i < 80; i++)
        {
            builder.Clear();

            for (int w = 0; w < 12; w++)
                builder.Append(words[random.Next(words.Length)]).Append(' ');

            index.Add(new SearchDocument("doc-" + i.ToString("D3", CultureInfo.InvariantCulture), builder.ToString()));
        }

        return index;
    }
}
