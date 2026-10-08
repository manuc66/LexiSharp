using System;
using System.Globalization;
using System.IO;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// A segment held as a memory-mapped file is searched like any other: the same page as the index it was
/// written from, and the map disposed safely when it is done.
/// </summary>
public class MappedSegmentTests
{
    [Fact]
    public void AMappedSegmentSearchesLikeTheIndexItWasWrittenFrom()
    {
        var index = Build();
        var memory = new RankedTextSearchEngine(index, new Bm25Scorer());
        string path = Path.Combine(Path.GetTempPath(), "lexisharp-mapped-" + Guid.NewGuid().ToString("N") + ".lxs");

        try
        {
            File.WriteAllBytes(path, SegmentWriter.Write(index));

            using (var mapped = MappedSegment.Open(path))
            {
                var mappedEngine = new RankedTextSearchEngine(mapped.Index, new Bm25Scorer());

                foreach (string query in new[] { "search", "search engine", "index query score", "quokka" })
                {
                    var expected = memory.Search(query);
                    var actual = mappedEngine.Search(query);

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

            // Dispose is idempotent, which the second call checks.
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static InMemoryTextIndex Build()
    {
        var index = new InMemoryTextIndex();
        var random = new Random(17);
        var words = new[] { "search", "engine", "index", "query", "score", "rank", "document", "token", "term" };
        var builder = new System.Text.StringBuilder();

        for (int i = 0; i < 100; i++)
        {
            builder.Clear();

            for (int w = 0; w < 12; w++)
                builder.Append(words[random.Next(words.Length)]).Append(' ');

            index.Add(new SearchDocument("doc-" + i.ToString("D3", CultureInfo.InvariantCulture), builder.ToString()));
        }

        return index;
    }
}