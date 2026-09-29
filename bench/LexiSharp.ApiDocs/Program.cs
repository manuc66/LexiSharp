using System.Globalization;

namespace LexiSharp.ApiDocs;

/// <summary>
/// Generates the API reference page from the compiled assemblies, and checks the committed page
/// against them.
/// </summary>
/// <remarks>
/// Internal build tooling, not a shipped library. It writes one Markdown file and reads the
/// build output; it never touches the source tree. The page it produces is committed, and
/// <c>check</c> is what stops it from drifting: a public type added without a regenerated page
/// fails the build, which is the moment a new type is still cheap to document.
/// </remarks>
internal static class Program
{
    /// <summary>
    /// The packages the page covers, in the order they are listed. Written out rather than
    /// globbed: a glob over <c>bin</c> silently drops a package that the current build did not
    /// produce, and a reference page missing a package is worse than a build that stops.
    /// </summary>
    private static readonly string[] Packages = ["LexiSharp", "LexiSharp.MessagePack", "LexiSharp.Postgres", "LexiSharp.AspNetCore"];

    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"api-docs: {exception.Message}");
            return 2;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Usage(Console.Error);
            return 2;
        }

        string verb = args[0];
        string root = ValueOf(args, "--root") ?? ".";
        string output = Path.Combine(root, "docs", "api.md");

        return verb switch
        {
            "write" => Write(root, output),
            "check" => Check(root, output),
            "print" => Print(root),
            _ => Unknown(verb),
        };
    }

    /// <summary>Generates the page and writes it, creating the file if it does not exist yet.</summary>
    private static int Write(string root, string output)
    {
        var page = Generate(root);

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, page, new System.Text.UTF8Encoding(false));

        Console.Error.WriteLine($"{Summary(page)} -> {Relative(root, output)}");
        return 0;
    }

    /// <summary>
    /// Compares the committed page with the one the assemblies produce, and fails on any byte of
    /// difference. The first differing line is reported: a CI log that says only "out of date"
    /// sends the reader to a diff on another machine, and a local run of this is the point where
    /// that diff should already be on screen.
    /// </summary>
    private static int Check(string root, string output)
    {
        string expected = Generate(root);

        if (!File.Exists(output))
        {
            Console.Error.WriteLine($"api-docs: {Relative(root, output)} does not exist.");
            Console.Error.WriteLine("api-docs: run `dotnet run --project bench/LexiSharp.ApiDocs -c Release -- write`.");
            return 1;
        }

        string actual = File.ReadAllText(output);

        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"{Summary(expected)}; {Relative(root, output)} is up to date.");
            return 0;
        }

        ReportDifference(Relative(root, output), actual, expected);
        Console.Error.WriteLine("api-docs: run `dotnet run --project bench/LexiSharp.ApiDocs -c Release -- write`.");
        return 1;
    }

    /// <summary>Writes the page to stdout, for a look at it without touching the tree.</summary>
    private static int Print(string root)
    {
        var page = Generate(root);

        Console.Error.WriteLine(Summary(page));
        Console.Out.Write(page);
        return 0;
    }

    private static string Generate(string root)
    {
        var assemblies = new List<(string Package, IReadOnlyList<ApiType> Types)>(Packages.Length);
        var missing = new List<string>();

        foreach (string package in Packages)
        {
            string assembly = Path.Combine(root, "src", package, "bin", "Release", "net10.0", package + ".dll");

            if (!File.Exists(assembly))
            {
                missing.Add(package);
                continue;
            }

            assemblies.Add((package, ApiSurface.Read(assembly)));
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"no Release build for {string.Join(", ", missing)}. " +
                "Build the solution in Release first: dotnet build LexiSharp.slnx -c Release");
        }

        return ApiReference.Render(assemblies);
    }

    /// <summary>
    /// The first line that differs, with both versions, and how many lines were added and
    /// removed. The counts come from a longest-common-subsequence diff rather than from comparing
    /// the two files line by line: removing one row shifts every line after it, so an index-wise
    /// comparison reports the whole tail as changed — "249 lines differ" for a page that lost five
    /// types is a wrong reading of a real change, which is worse than no number at all.
    /// </summary>
    private static void ReportDifference(string path, string actual, string expected)
    {
        var difference = new LineDiff(actual.Split('\n'), expected.Split('\n'));

        if (!difference.Changed)
            return;

        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"api-docs: {path} is out of date ({difference.Removed} line(s) removed, {difference.Added} added, first at line {difference.FirstChangedLine})."));
        Console.Error.WriteLine($"  committed: {difference.FirstCommitted}");
        Console.Error.WriteLine($"  generated: {difference.FirstGenerated}");
    }

    /// <summary>How big the page is, for a build log. A row is a line that starts a table entry.</summary>
    private static string Summary(string page)
    {
        int rows = page.Split('\n').Count(line => line.StartsWith("| `", StringComparison.Ordinal));
        return $"{rows} rows ({page.Length} bytes)";
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));

    private static string? ValueOf(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
                return args[i + 1];
        }

        return null;
    }

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"api-docs: unknown command '{verb}'");
        Usage(Console.Error);
        return 2;
    }

    private static void Usage(TextWriter writer)
    {
        writer.WriteLine(
            """
            usage:
              api-docs write [--root <dir>]   regenerate docs/api.md from the Release assemblies
              api-docs check [--root <dir>]   exit 1 if docs/api.md differs from them
              api-docs print [--root <dir>]   write the page to stdout, leaving the tree alone

            Requires `dotnet build LexiSharp.slnx -c Release` first: the page is read out of the
            compiled assemblies and their XML documentation, not out of the source.
            """.ReplaceLineEndings("\n"));
    }
}
