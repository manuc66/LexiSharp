using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using LexiSharp.Eval;
using Xunit;

namespace LexiSharp.Tests;

public class ClassificationEvalTests
{
    [Fact]
    public void ParseMessage_DropsHeadersAndKeepsSubjectAndBody()
    {
        const string raw =
            "From: someone@example.com\n" +
            "Subject: Re: the subject\n" +
            "Newsgroups: alt.atheism\n" +
            "\n" +
            "The body.\n";

        string text = TwentyNewsgroups.ParseMessage(raw);

        Assert.Equal("Re: the subject\nThe body.", text);
        Assert.DoesNotContain("From:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Newsgroups:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseMessage_WithoutSubject_ReturnsTheBody()
    {
        const string raw = "From: someone@example.com\n\nBody only\n";

        Assert.Equal("Body only", TwentyNewsgroups.ParseMessage(raw));
    }

    [Fact]
    public void ParseMessage_NormalizesCrLf()
    {
        const string raw = "Subject: title\r\n\r\nbody\r\n";

        Assert.Equal("title\nbody", TwentyNewsgroups.ParseMessage(raw));
    }

    [Fact]
    public void Extract_WritesTheArchiveEntries()
    {
        string root = Path.Combine(Path.GetTempPath(), "lexisharp-20ng-" + Guid.NewGuid().ToString("N"));
        string archive = Path.Combine(root, "sample.tar.gz");
        string destination = Path.Combine(root, "out");

        try
        {
            Directory.CreateDirectory(root);
            string source = Path.Combine(root, "source.txt");
            File.WriteAllText(source, "hello");

            using (FileStream file = File.Create(archive))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            using (var writer = new TarWriter(gzip, TarEntryFormat.Pax))
                writer.WriteEntry(source, "corpus/category/1");

            TwentyNewsgroups.Extract(archive, destination);

            Assert.Equal("hello", File.ReadAllText(Path.Combine(destination, "corpus", "category", "1")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Extract_RejectsAnEntryThatEscapesTheDestination()
    {
        string root = Path.Combine(Path.GetTempPath(), "lexisharp-20ng-" + Guid.NewGuid().ToString("N"));
        string archive = Path.Combine(root, "sample.tar.gz");
        string destination = Path.Combine(root, "out");

        try
        {
            Directory.CreateDirectory(root);
            string source = Path.Combine(root, "source.txt");
            File.WriteAllText(source, "escape");

            using (FileStream file = File.Create(archive))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            using (var writer = new TarWriter(gzip, TarEntryFormat.Pax))
                writer.WriteEntry(source, "../escape.txt");

            Assert.Throws<InvalidOperationException>(() => TwentyNewsgroups.Extract(archive, destination));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Score_ComputesAccuracyAndMacroF1()
    {
        var test = new[]
        {
            new ClassificationDocument("1", "a", "A"),
            new ClassificationDocument("2", "b", "B"),
            new ClassificationDocument("3", "c", "B"),
        };
        string?[] predictions = { "A", "A", "B" };

        var metrics = ClassificationEvaluation.Score(
            "test",
            test,
            predictions,
            averageQuestions: double.NaN,
            TimeSpan.Zero,
            TimeSpan.Zero);

        Assert.Equal(3, metrics.Documents);
        Assert.Equal(2.0 / 3.0, metrics.Accuracy, precision: 10);
        Assert.Equal(2.0 / 3.0, metrics.MacroF1, precision: 10);
        Assert.True(double.IsNaN(metrics.AverageQuestions));
    }

    [Fact]
    public void Score_ComputesPerClassF1AndAStablePredictionHash()
    {
        var test = new[]
        {
            new ClassificationDocument("1", "a", "A"),
            new ClassificationDocument("2", "b", "B"),
            new ClassificationDocument("3", "c", "B"),
        };
        string?[] predictions = { "A", "A", "B" };

        var metrics = ClassificationEvaluation.Score(
            "test",
            test,
            predictions,
            averageQuestions: double.NaN,
            TimeSpan.Zero,
            TimeSpan.Zero);

        Assert.Equal(2.0 / 3.0, metrics.PerClassF1["A"], precision: 10);
        Assert.Equal(2.0 / 3.0, metrics.PerClassF1["B"], precision: 10);
        Assert.Equal(metrics.PredictionHash, ClassificationEvaluation.HashPredictions(test, predictions));
        Assert.NotEqual(
            metrics.PredictionHash,
            ClassificationEvaluation.HashPredictions(test, new string?[] { "A", "B", "B" }));
    }

    [Fact]
    public void McNemar_WithNoDiscordance_ReturnsOne()
    {
        var test = new[]
        {
            new ClassificationDocument("1", "a", "A"),
            new ClassificationDocument("2", "b", "B"),
        };
        string?[] predictions = { "A", "B" };

        var result = ClassificationEvaluation.McNemar(
            test, predictions, predictions, "baseline", "candidate");

        Assert.Equal(0, result.BaselineOnly);
        Assert.Equal(0, result.CandidateOnly);
        Assert.Equal(1.0, result.PValue, precision: 12);
    }

    [Fact]
    public void McNemar_WithABaselineOnlyAdvantage_ReturnsASmallP()
    {
        var test = new List<ClassificationDocument>();
        var baseline = new List<string?>();
        var candidate = new List<string?>();

        for (int i = 0; i < 10; i++)
        {
            test.Add(new ClassificationDocument($"d{i}", "x", "A"));
            baseline.Add("A");
            candidate.Add("B");
        }

        var result = ClassificationEvaluation.McNemar(
            test, baseline, candidate, "baseline", "candidate");

        Assert.Equal(10, result.BaselineOnly);
        Assert.Equal(0, result.CandidateOnly);
        Assert.True(result.PValue < 0.01);
    }

    [Fact]
    public void Score_OnPerfectPredictions_ReadsOne()
    {
        var test = new[]
        {
            new ClassificationDocument("1", "a", "A"),
            new ClassificationDocument("2", "b", "B"),
        };
        string?[] predictions = { "A", "B" };

        var metrics = ClassificationEvaluation.Score(
            "test",
            test,
            predictions,
            averageQuestions: 2,
            TimeSpan.Zero,
            TimeSpan.Zero);

        Assert.Equal(1.0, metrics.Accuracy, precision: 10);
        Assert.Equal(1.0, metrics.MacroF1, precision: 10);
    }

    [Fact]
    public void Score_WithTheWrongPredictionCount_Throws()
    {
        var test = new[] { new ClassificationDocument("1", "a", "A") };
        string?[] predictions = Array.Empty<string?>();

        Assert.Throws<ArgumentException>(() => ClassificationEvaluation.Score(
            "test",
            test,
            predictions,
            averageQuestions: double.NaN,
            TimeSpan.Zero,
            TimeSpan.Zero));
    }
}
