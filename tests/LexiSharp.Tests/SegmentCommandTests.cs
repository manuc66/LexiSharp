using System.Text.Json;
using LexiSharp.Cli;
using LexiSharp.Indexing;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The <c>segment</c> front end, exercised through its own argument parsing and its own store.
/// </summary>
/// <remarks>
/// The command writes to <see cref="Console"/>, so the tests that read its output run in a
/// non-parallel collection: a <c>Console.SetOut</c> in one test is process-global, and a second test
/// writing concurrently would land in the first one's writer.
/// </remarks>
[CollectionDefinition("segment-cli", DisableParallelization = true)]
public sealed class SegmentCliCollection
{
}

[Collection("segment-cli")]
public class SegmentCommandTests
{
    private static string CorpusRoot()
    {
        // Walk up to the repository root rather than trusting a relative path through bin/, which
        // changes shape with the target framework and the configuration.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LexiSharp.slnx")))
                return Path.Combine(directory.FullName, "bench", "reference-corpus", "corpus");

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }

    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), "lexisharp-cli-" + Guid.NewGuid().ToString("N"));

    private static (int ExitCode, string Output) Run(params string[] args)
    {
        var outWriter = new StringWriter();
        var errorWriter = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;

        try
        {
            Console.SetOut(outWriter);
            Console.SetError(errorWriter);
            int exitCode = SegmentCommands.Run(args);

            return (exitCode, outWriter.ToString() + errorWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static (int ExitCode, string Output) RunProgram(params string[] args)
    {
        var outWriter = new StringWriter();
        var errorWriter = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;

        try
        {
            Console.SetOut(outWriter);
            Console.SetError(errorWriter);
            int exitCode = LexiSharp.Cli.Program.Main(args);

            return (exitCode, outWriter.ToString() + errorWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void BuildThenSearchServesTheCommittedCorpus()
    {
        string store = NewDirectory();

        try
        {
            var build = Run("build", CorpusRoot(), "--store", store);
            Assert.Equal(0, build.ExitCode);
            Assert.Contains("Built 42 documents", build.Output, StringComparison.Ordinal);

            var search = Run("search", store, "--query", "pagination window", "--top-k", "3");
            Assert.Equal(0, search.ExitCode);
            Assert.Contains("pagination-window.md", search.Output, StringComparison.Ordinal);

            // What the command wrote is what a fresh reader opens, without being handed the documents.
            using var reopened = new SegmentedTextIndex(store);
            Assert.Equal(42, reopened.Count);
            Assert.Equal(1, reopened.SegmentCount);
        }
        finally
        {
            if (Directory.Exists(store)) Directory.Delete(store, recursive: true);
        }
    }

    [Fact]
    public void RemoveTombstonesAndCommits()
    {
        string store = NewDirectory();

        try
        {
            Assert.Equal(0, Run("build", CorpusRoot(), "--store", store).ExitCode);
            Assert.Equal(0, Run("search", store, "--query", "pagination window", "--top-k", "3").ExitCode);

            var remove = Run("remove", store, "--id", "ordinary/pagination-window.md");
            Assert.Equal(0, remove.ExitCode);
            Assert.Contains("41 documents remain", remove.Output, StringComparison.Ordinal);

            using var reopened = new SegmentedTextIndex(store);
            Assert.Equal(41, reopened.Count);
            Assert.Equal(0, reopened.TombstoneCount);

            var after = Run("search", store, "--query", "pagination window", "--top-k", "3");
            Assert.DoesNotContain("pagination-window.md", after.Output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(store)) Directory.Delete(store, recursive: true);
        }
    }

    [Fact]
    public void InfoReportsTheStoreWithoutSearching()
    {
        string store = NewDirectory();

        try
        {
            Assert.Equal(0, Run("build", CorpusRoot(), "--store", store).ExitCode);

            var info = Run("info", store);
            Assert.Equal(0, info.ExitCode);
            Assert.Contains("Documents:   42", info.Output, StringComparison.Ordinal);
            Assert.Contains("Segments:    1", info.Output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(store)) Directory.Delete(store, recursive: true);
        }
    }

    [Fact]
    public void BuildRecordsTheAnalyzerAndEveryReadUsesIt()
    {
        // The dictionary holds normalized terms, so a store searched with a different tokenizer reads
        // a different index. The manifest is what makes that impossible from the CLI: the analyzer is
        // recorded at build time and every later subcommand reads the recorded one, so there is no
        // flag that could disagree with it.
        string store = NewDirectory();

        try
        {
            Assert.Equal(0, Run("build", CorpusRoot(), "--store", store, "--analyzer", "english").ExitCode);

            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(store, "store.json")));
            Assert.Equal("english", manifest.RootElement.GetProperty("Analyzer").GetString());

            // "windows" reaches the document titled "... window" only under stemming, so this is the
            // english tokenizer being used rather than the default one.
            var search = Run("search", store, "--query", "windows", "--top-k", "1");
            Assert.Equal(0, search.ExitCode);
            Assert.Contains("window", search.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(store)) Directory.Delete(store, recursive: true);
        }
    }

    [Fact]
    public void ACommitReclaimsTombstonesAndTheCorpusSurvives()
    {
        string store = NewDirectory();

        try
        {
            Assert.Equal(0, Run("build", CorpusRoot(), "--store", store).ExitCode);
            Assert.Equal(0, Run("remove", store, "--id", "ordinary/pagination-window.md").ExitCode);

            var commit = Run("commit", store);
            Assert.Equal(0, commit.ExitCode);
            Assert.Contains("in 1 segment on disk", commit.Output, StringComparison.Ordinal);

            using var reopened = new SegmentedTextIndex(store);
            Assert.Equal(41, reopened.Count);
        }
        finally
        {
            if (Directory.Exists(store)) Directory.Delete(store, recursive: true);
        }
    }

    [Fact]
    public void ADirectoryThatIsNotAStoreIsRefused()
    {
        string store = NewDirectory();

        try
        {
            var search = Run("search", store, "--query", "anything");

            Assert.Equal(1, search.ExitCode);
            Assert.Contains("Not a store", search.Output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(store)) Directory.Delete(store, recursive: true);
        }
    }

    [Fact]
    public void AMissingArgumentIsAUsageError()
    {
        Assert.Equal(2, Run("build").ExitCode);
        Assert.Equal(2, Run("search", NewDirectory()).ExitCode);
        Assert.Equal(2, Run("nonsense").ExitCode);
    }

    // ---- where a search finds its store ---------------------------------------------------------

    [Fact]
    public void SearchUsesTheStoreInTheFolderItWasGiven()
    {
        // A store directory read as a corpus would index its own segment files, and `build` then
        // `search` is the ordinary sequence, so the store wins over a scan of the folder holding it.
        string folder = NewDirectory();
        string store = Path.Combine(folder, ".lexisharp");

        try
        {
            Assert.Equal(0, Run("build", CorpusRoot(), "--store", store).ExitCode);

            var search = RunProgram("search", folder, "--query", "pagination window", "--top-k", "3");

            Assert.Equal(0, search.ExitCode);
            Assert.Contains("Store:", search.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Corpus:", search.Output, StringComparison.Ordinal);
            Assert.Contains("pagination-window.md", search.Output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void SearchFindsTheStoreInTheCurrentFolderWhenNoDirectoryIsGiven()
    {
        string folder = NewDirectory();
        string originalDirectory = Directory.GetCurrentDirectory();

        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(
                Path.Combine(folder, "note.txt"),
                "reciprocal rank fusion merges several ranked lists");

            Directory.SetCurrentDirectory(folder);
            Assert.Equal(0, Run("build", folder).ExitCode);
            Assert.True(File.Exists(Path.Combine(folder, ".lexisharp", "store.json")));

            var search = RunProgram("search", "--query", "rank fusion", "--top-k", "3");

            Assert.Equal(0, search.ExitCode);
            Assert.Contains("Store:", search.Output, StringComparison.Ordinal);
            Assert.Contains("note.txt", search.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void SearchScansTheFolderWhenThereIsNoStore()
    {
        string folder = NewDirectory();

        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "note.txt"), "reciprocal rank fusion merges ranked lists");

            var search = RunProgram("search", folder, "--query", "rank fusion");

            Assert.Equal(0, search.ExitCode);
            Assert.Contains("Corpus:", search.Output, StringComparison.Ordinal);
            Assert.Contains("note.txt", search.Output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void SearchRefusesAnAnalyzerAgainstAStore()
    {
        // The store records the analyzer it was built with. A flag that quietly disagreed with it
        // would read a different index, which is the failure the manifest exists to prevent.
        string store = NewDirectory();

        try
        {
            Assert.Equal(0, Run("build", CorpusRoot(), "--store", store).ExitCode);

            var search = RunProgram("search", store, "--query", "pagination", "--analyzer", "english");

            Assert.Equal(2, search.ExitCode);
            Assert.Contains("--analyzer is not accepted", search.Output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(store)) Directory.Delete(store, recursive: true);
        }
    }
}
