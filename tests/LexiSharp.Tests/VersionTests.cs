using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Keeps the package version declared in one place, and that one place effective.
/// </summary>
/// <remarks>
/// <para>
/// The version was a <c>&lt;Version&gt;</c> element in each of the four packable project files, and
/// the same number was written in prose in the README and two documentation pages. A release then
/// meant editing five files and the only thing that noticed a miss was a reader: <c>5248329</c> is
/// a commit whose entire content is the version being three pages stale after a release. Nothing in
/// the build could fail over a number that only prose disagreed with.
/// </para>
/// <para>
/// It is now a single property in <c>Directory.Build.props</c>, which is imported before every
/// project body. That placement is the substance, not tidiness: an explicit <c>&lt;Version&gt;</c>
/// in a project file still wins, silently, so a version that only <em>looks</em> centralized is
/// worse than one that was visibly duplicated. The first test below is what keeps the duplication
/// from coming back; the second is what proves the property still reaches the artifacts.
/// </para>
/// <para>
/// The prose in the README and the docs is deliberately <em>not</em> pinned here. It states the
/// current released version, so between two releases the number is correct and between the version
/// bump and the tag it is stale by construction. A test that went red for that window would be
/// teaching its reader to ignore it, which is worse than the defect it covers. Those pages are
/// updated as part of the release, by the person making it.
/// </para>
/// </remarks>
public partial class VersionTests
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

    /// <summary>The shipped packages, by assembly name. All four are referenced by this project.</summary>
    private static readonly string[] ShippedAssemblies =
    [
        "LexiSharp",
        "LexiSharp.AspNetCore",
        "LexiSharp.MessagePack",
        "LexiSharp.Postgres",
    ];

    [GeneratedRegex(@"<Version>(?<value>[^<]*)</Version>")]
    private static partial Regex VersionElement();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlComment();

    [Fact]
    public void TheVersionIsDeclaredOnce_InDirectoryBuildProps()
    {
        var declarations = new List<string>();

        foreach (string path in SourceFiles())
        {
            if (VersionElement().IsMatch(WithoutComments(path)))
                declarations.Add(Path.GetRelativePath(RepositoryRoot, path));
        }

        Assert.True(
            declarations.Count == 1,
            $"the package version is declared in {declarations.Count} file(s): {string.Join(", ", declarations)}. " +
            "Declare it once, in Directory.Build.props, and remove it from the others.");

        Assert.Equal(
            Path.Combine("Directory.Build.props"),
            declarations[0],
            StringComparer.Ordinal);
    }

    [Fact]
    public void TheDeclaredVersionReachesEveryShippedAssembly()
    {
        string declared = DeclaredVersion();

        foreach (string name in ShippedAssemblies)
        {
            string path = Path.Combine(AppContext.BaseDirectory, name + ".dll");
            Assert.True(File.Exists(path), $"{name}.dll is not next to the test assembly, so the version cannot be read from the artifact.");

            var info = FileVersionInfo.GetVersionInfo(path);
            string? product = info.ProductVersion;

            Assert.False(
                string.IsNullOrEmpty(product),
                $"{name} carries no ProductVersion, so nothing can say which version it is.");

            Assert.True(
                product.StartsWith(declared, StringComparison.Ordinal),
                $"{name} reports ProductVersion '{product}', which does not start with the declared '{declared}'. " +
                "Either the property is not reaching the project, or a project file overrides it.");
        }
    }

    [Fact]
    public void TheDeclaredVersionIsAThreePartNumber()
    {
        // Nothing about the public API is frozen before 1.0, and the version is where that is
        // written down. The test does not assert a particular number: it asserts the shape, so a
        // typo that would make the package unreleasable fails here rather than at the tag.
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", DeclaredVersion());
    }

    private static string DeclaredVersion()
    {
        string props = Path.Combine(RepositoryRoot, "Directory.Build.props");
        Match match = VersionElement().Match(WithoutComments(props));

        Assert.True(match.Success, "Directory.Build.props declares no <Version>. The four project files stopped declaring one when it moved here, so nothing is versioned.");
        return match.Groups["value"].Value;
    }

    /// <summary>
    /// The file with its XML comments removed. The comment explaining this property talks about
    /// <c>&lt;Version&gt;</c> elements, so a scan that read the comments would either count the
    /// explanation as a second declaration or, worse, pass for the wrong reason.
    /// </summary>
    private static string WithoutComments(string path) =>
        XmlComment().Replace(File.ReadAllText(path), string.Empty);

    /// <summary>Build and source files, plus the props, skipping build output and the git directory.</summary>
    private static IEnumerable<string> SourceFiles()
    {
        foreach (string pattern in new[] { "*.csproj", "*.props", "*.targets" })
        {
            foreach (string path in Directory.EnumerateFiles(RepositoryRoot, pattern, SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(RepositoryRoot, path);

                // obj/ and bin/ hold generated copies that would double every count, and .git holds
                // loose objects whose names are not the paths they came from.
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
