using System.Text.RegularExpressions;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Holds the build to failing on a warning, and keeps it doing that.
/// </summary>
/// <remarks>
/// <para>
/// <c>TreatWarningsAsErrors</c> is declared once, in <c>Directory.Build.props</c>, and the only
/// reason a reader needs this file is that nothing in the build would notice its removal: a
/// <c>dotnet build</c> step fails on errors only, so deleting the property turns the gate off
/// silently and the next CS1591 enters the tree unnoticed. A gate whose absence has no symptom is
/// not a gate.
/// </para>
/// <para>
/// This asserts the declaration, not the behaviour. Whether a warning really does fail the build is
/// answered by the build itself: every project in the solution compiles with the property applied,
/// and a diagnostic anywhere — a missing <c>&lt;summary&gt;</c>, a CA rule an SDK bump newly
/// enables, a NU1903 raised by an advisory refresh — is an error in each of the workflows.
/// </para>
/// </remarks>
public partial class BuildWarningGateTests
{
    private static string RepositoryRoot
    {
        get
        {
            // The test assembly runs out of tests/.../bin/<config>/<tfm>; walk up to the root.
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LexiSharp.slnx")))
                directory = directory.Parent;

            Assert.NotNull(directory);
            return directory.FullName;
        }
    }

    [GeneratedRegex(@"<TreatWarningsAsErrors>(?<value>[^<]*)</TreatWarningsAsErrors>")]
    private static partial Regex WarningGateElement();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlComment();

    [Fact]
    public void TheWarningGateIsDeclaredOnce_AndItIsOn()
    {
        var declarations = new List<string>();

        foreach (string path in BuildFiles())
        {
            if (WarningGateElement().IsMatch(WithoutComments(path)))
                declarations.Add(Path.GetRelativePath(RepositoryRoot, path));
        }

        Assert.True(
            declarations.Count == 1,
            $"TreatWarningsAsErrors is declared in {declarations.Count} file(s): {string.Join(", ", declarations)}. " +
            "Declare it once, in Directory.Build.props, and remove it from the others: an explicit element " +
            "in a project file overrides the imported one silently, so a gate that only looks global " +
            "governs the projects whose owner remembered it.");

        string props = Path.Combine(RepositoryRoot, "Directory.Build.props");
        Match match = WarningGateElement().Match(WithoutComments(props));

        Assert.True(
            match.Groups["value"].Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase),
            $"Directory.Build.props sets TreatWarningsAsErrors to '{match.Groups["value"].Value}', so a warning " +
            "compiles and nothing reads it. Set it to true.");
    }

    /// <summary>
    /// The file with its XML comments removed. The comment explaining the property discusses what a
    /// warning does to the build, so a scan that read the comments could match the explanation
    /// instead of the declaration, or count the explanation as a second one.
    /// </summary>
    private static string WithoutComments(string path) =>
        XmlComment().Replace(File.ReadAllText(path), string.Empty);

    /// <summary>Build files, skipping build output and the git directory.</summary>
    private static IEnumerable<string> BuildFiles()
    {
        foreach (string pattern in new[] { "*.csproj", "*.props", "*.targets" })
        {
            foreach (string path in Directory.EnumerateFiles(RepositoryRoot, pattern, SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(RepositoryRoot, path);

                // obj/ holds the imported copies of Directory.Build.props and the generated nuget
                // g.props, which would count each declaration twice.
                if (relative.StartsWith(".git", StringComparison.Ordinal) ||
                    relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return path;
            }
        }
    }
}