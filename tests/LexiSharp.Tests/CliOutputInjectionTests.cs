using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The tool prints corpus text to a terminal: a document's text, its id, and the query. A corpus is
/// input — a transcript, a scraped page, a downloaded dataset — and a terminal is an interpreter, so
/// a byte in that text is a command. `ESC[2J` clears the screen, `ESC]0;…BEL` sets the title,
/// `ESC]52;c;…BEL` writes the clipboard, and none of it is visible in the output.
/// </summary>
/// <remarks>
/// These run the real front end through <see cref="Program.Main"/> and read its stdout, because the
/// property is about what the process writes, not about an intermediate string. The console is
/// process-global, so the class joins the non-parallel collection the other console test uses.
/// </remarks>
[Collection("segment-cli")]
public class CliOutputInjectionTests
{
    private const string HostileText =
        "another alpha note \u001b[2J\u001b]0;title-set-by-document\u0007\u001b]52;c;cGF5bG9hZA==\u0007 end";

    [Fact]
    public void Search_DoesNotWriteTheEscapeSequencesADocumentCarries()
    {
        string corpus = CreateCorpus(("a.txt", "a note about alpha"), ("b.txt", HostileText));

        string output = CaptureSearch(corpus, "alpha");

        AssertNothingInvisible(output);
        // The words around the sequences still have to be there: a sanitizer that dropped the whole
        // document would pass the assertion above and fail the reader.
        Assert.Contains("another alpha note", output);
        Assert.Contains("end", output);
    }

    [Fact]
    public void Search_DoesNotWriteTheEscapeSequencesTheQueryCarries()
    {
        string corpus = CreateCorpus(("a.txt", "a note about alpha"));

        string output = CaptureSearch(corpus, "alpha\u001b[2J");

        AssertNothingInvisible(output);
        Assert.Contains("alpha", output);
    }

    [Fact]
    public void Search_DoesNotWriteTheBidiOverrideADocumentIdCarries()
    {
        // The id is printed beside the score, so it is a sink like the text — and a right-to-left
        // override is the same problem one layer up: text that says one thing and shows another.
        string corpus = CreateCorpus(("gpj\u202egnp.txt", "a note about alpha"));

        string output = CaptureSearch(corpus, "alpha");

        Assert.DoesNotContain('\u202e', output);
        Assert.Contains("a note about alpha", output);
    }

    private static void AssertNothingInvisible(string output)
    {
        Assert.DoesNotContain('\u001b', output);   // ESC: the introducer of every sequence
        Assert.DoesNotContain('\u009b', output);   // 8-bit CSI
        Assert.DoesNotContain('\u009d', output);   // 8-bit OSC
        Assert.DoesNotContain('\u0007', output);   // BEL, the terminator those sequences need
        Assert.DoesNotContain('\u202e', output);   // right-to-left override
    }

    private static string CreateCorpus(params (string Name, string Text)[] files)
    {
        string root = Path.Combine(Path.GetTempPath(), "lexisharp-cli-injection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        foreach (var (name, text) in files)
            File.WriteAllText(Path.Combine(root, name), text);

        return root;
    }

    private static string CaptureSearch(string corpus, string query)
    {
        TextWriter previous = Console.Out;
        var capture = new StringWriter();

        try
        {
            Console.SetOut(capture);
            int exit = LexiSharp.Cli.Program.Main(["search", corpus, "--query", query, "--top-k", "5"]);
            Assert.Equal(0, exit);
        }
        finally
        {
            Console.SetOut(previous);
        }

        return capture.ToString();
    }
}
