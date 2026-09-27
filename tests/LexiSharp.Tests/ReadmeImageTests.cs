using System.Text.RegularExpressions;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Guards the image references in the README.
/// </summary>
/// <remarks>
/// <para>
/// The README is also the NuGet package readme (<c>PackageReadmeFile</c> in every project), and
/// the package ships the README alone -- the <c>docs/images</c> folder is not inside the
/// <c>.nupkg</c>, verified by listing the archive. A repository-relative path therefore cannot
/// resolve for a reader on nuget.org, so the screenshots are referenced by absolute URL instead.
/// </para>
/// <para>
/// Those absolute URLs are the one kind of link that rots without anything in the repository
/// noticing: delete or rename an image and the README keeps pointing at a <c>main</c> that no
/// longer has it, while the tests stay green. So the paths are resolved against the working tree.
/// </para>
/// <para>
/// Only repository-hosted images are checked. The status badges at the top of the README are
/// images too, but they are served by third parties (shields.io, GitHub Actions, codecov,
/// SonarCloud) and have no local counterpart to assert on.
/// </para>
/// </remarks>
public partial class ReadmeImageTests
{
    private const string RawContentHost = "raw.githubusercontent.com";

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

    [Fact]
    public void EveryRepositoryHostedImageResolvesToAFileInTheRepository()
    {
        var broken = new List<string>();

        foreach (Match match in ImageReferenceRegex().Matches(Readme))
        {
            string url = match.Groups["url"].Value;

            if (!TryGetRepositoryPath(url, out string? path))
                continue; // third-party badge: nothing local to assert on

            string local = Path.Combine(RepositoryRoot, path);

            if (!File.Exists(local))
                broken.Add($"{url} (expected at {local})");
        }

        Assert.True(
            broken.Count == 0,
            "The README points at images the repository does not contain. Commit them, or fix the " +
            $"reference: {string.Join("; ", broken)}");
    }

    [Fact]
    public void NoReadmeImageUsesARepositoryRelativePath()
    {
        // A relative path is the exact mistake that made the screenshots render on GitHub and
        // nowhere else, so it is asserted rather than left to review.
        var relative = ImageReferenceRegex()
            .Matches(Readme)
            .Select(m => m.Groups["url"].Value)
            .Where(url => !url.Contains("://", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            relative.Count == 0,
            "The README ships as the NuGet package readme, where a repository-relative image path " +
            $"cannot resolve. Use an absolute URL instead: {string.Join(", ", relative)}");
    }

    [Fact]
    public void EveryReadmeImageCarriesAlternativeText()
    {
        // Alt text is what a screen reader announces, and what a NuGet reader falls back to when an
        // image does load, so an empty one throws the description away.
        var withoutAlt = ImageReferenceRegex()
            .Matches(Readme)
            .Where(m => string.IsNullOrWhiteSpace(m.Groups["alt"].Value))
            .Select(m => m.Groups["url"].Value)
            .ToList();

        Assert.True(
            withoutAlt.Count == 0,
            $"These README images have no alternative text: {string.Join(", ", withoutAlt)}");
    }

    [Fact]
    public void RepositoryHostDetectionRecognisesRawContentUrls()
    {
        // Guards the guard: if this stopped recognising raw.githubusercontent.com, the check above
        // would silently pass by treating every screenshot as a third-party badge.
        Assert.True(TryGetRepositoryPath(
            "https://raw.githubusercontent.com/manuc66/LexiSharp/main/docs/images/demo.png",
            out string? path));
        Assert.Equal("docs/images/demo.png", path);

        Assert.False(TryGetRepositoryPath("https://img.shields.io/nuget/v/LexiSharp.svg", out _));
        Assert.False(TryGetRepositoryPath(
            "https://github.com/manuc66/lexisharp/actions/workflows/ci.yml/badge.svg", out _));
    }

    private static string Readme => File.ReadAllText(Path.Combine(RepositoryRoot, "README.md"));

    /// <summary>
    /// The repository-relative path an image URL points at, or <c>false</c> when the image is not
    /// hosted by this repository.
    /// </summary>
    private static bool TryGetRepositoryPath(string url, out string path)
    {
        path = string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (!uri.Host.Equals(RawContentHost, StringComparison.OrdinalIgnoreCase))
            return false;

        // /<owner>/<repo>/<ref>/<path...>: everything after the third segment is the file path.
        string[] segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 4)
            return false;

        path = string.Join('/', segments[3..]);
        return path.Length > 0;
    }

    [GeneratedRegex(@"!\[(?<alt>[^\]]*)\]\((?<url>[^)\s]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex ImageReferenceRegex();
}
