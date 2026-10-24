using System;
using System.IO;
using System.Linq;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// What a store does with a directory it did not finish writing: a truncated segment, and two
/// generations left by a commit interrupted between its rename and its cleanup.
/// </summary>
/// <remarks>
/// Both cases are reachable without a bug in the process that crashed — a disk that fills, a machine
/// that loses power, a container killed mid-commit — and both are the class of defect that only shows
/// up after a restart. The reader validates the header and every table offset on open; these tests say
/// what that validation is worth at the boundary, and whether the commit's own cleanup window is safe.
/// </remarks>
public class SegmentRecoveryTests
{
    [Fact]
    public void ATruncatedSegmentIsRefusedAsInvalidData()
    {
        string directory = NewDirectory();

        try
        {
            Write(Build(("a", "quokka quokka"), ("b", "wallaby")), directory, generation: 1);

            string file = Directory.GetFiles(directory, "*.lxs").Single();
            byte[] whole = File.ReadAllBytes(file);

            // Three cuts: inside the header, in the middle of the tables, and one byte short of the
            // whole file. The last is the one a torn write actually produces.
            foreach (int length in new[] { SegmentWriter.HeaderBytes - 1, whole.Length / 2, whole.Length - 1 })
            {
                File.WriteAllBytes(file, whole.AsSpan(0, length).ToArray());

                var failure = Record.Exception(() =>
                {
                    // Open *and* search: a segment whose tables fit but whose postings do not would
                    // otherwise open cleanly and fail in the middle of a query, which is the failure
                    // this asserts against.
                    var store = new SegmentedTextIndex(directory);
                    new RankedTextSearchEngine(store, new Bm25Scorer()).Search("quokka");
                });

                Assert.NotNull(failure);
                Assert.IsType<InvalidDataException>(failure);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TwoGenerationsLeftByAnInterruptedCommitDoNotReturnADocumentTwice()
    {
        // A commit writes a temporary file, renames it into place, then deletes the segments it
        // replaced. A crash between the rename and the cleanup leaves both, and the constructor opens
        // every segment it finds.
        string directory = NewDirectory();

        try
        {
            Write(Build(("a", "quokka"), ("b", "quokka"), ("c", "wallaby")), directory, generation: 1);
            Write(Build(("a", "quokka"), ("b", "quokka")), directory, generation: 2);

            var store = new SegmentedTextIndex(directory);
            var engine = new RankedTextSearchEngine(store, new Bm25Scorer());

            string[] ids = engine.Search("quokka").Select(hit => hit.DocumentId).ToArray();

            // No document twice: both generations hold "a" and "b", and one hit per document is the
            // only reading under which "how many results" means what it says.
            Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());

            // And nothing comes back from the older generation. "c" existed before the commit and was
            // deleted by it; the successor is the flush of what was live, so serving "c" would be a
            // deletion undone by a crash.
            Assert.Empty(engine.Search("wallaby"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ADirectoryHoldingOnlyAnInterruptedCommitIsRefusedRatherThanOpenedEmpty()
    {
        // A temp segment and no readable generation is a commit that never completed. Opening it as a
        // new, empty store would answer "no documents" to every query while the corpus sits on disk —
        // the one failure worse than an exception.
        string directory = NewDirectory();

        try
        {
            Write(Build(("a", "quokka")), directory, generation: 1);

            string segment = Directory.GetFiles(directory, "*.lxs").Single();
            File.Move(segment, segment + ".tmp");

            Assert.Throws<InvalidDataException>(() => new SegmentedTextIndex(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AnEmptyDirectoryStillOpensAsANewStore()
    {
        // The refusal above must not turn a genuinely new store into an error: an empty directory is
        // what a first commit is written into.
        string directory = NewDirectory();

        try
        {
            var store = new SegmentedTextIndex(directory);

            Assert.Equal(0, store.Count);

            store.Add(new SearchDocument("a", "quokka"));
            Assert.Equal(1, store.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static InMemoryTextIndex Build(params (string Id, string Text)[] documents)
    {
        var index = new InMemoryTextIndex();

        foreach (var (id, text) in documents)
            index.Add(new SearchDocument(id, text));

        return index;
    }

    private static void Write(InMemoryTextIndex index, string directory, int generation)
    {
        Directory.CreateDirectory(directory);

        SegmentFile.Write(
            index,
            Path.Combine(directory, $"segment-{generation:D8}.lxs"));
    }

    private static string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "lexisharp-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
