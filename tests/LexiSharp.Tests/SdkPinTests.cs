using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Holds the build toolchain to one SDK version, and keeps it there.
/// </summary>
/// <remarks>
/// <para>
/// Every workflow asked for <c>dotnet-version: '10.0.x'</c>, and a floating range is a promise
/// about a future SDK rather than a constraint on the present one. It resolved to 10.0.111 on a
/// developer machine and to 10.0.401 on a GitHub-hosted runner — the same .NET 10, two feature
/// bands, two compilers. The difference was measurable before it was suspected: the code-shape
/// manifests reported 135 373 IL bytes for <c>LexiSharp.dll</c> on one host and 135 181 on the
/// other for the same 2 697 methods, which is a compiler emitting differently, not noise.
/// </para>
/// <para>
/// The quality numbers survived it, and the second test exists because they did: the pinned
/// ArguAna configurations measured 0.2709 and 0.3306 on both hosts, agreeing to the fourth
/// decimal across the two SDKs. So the drift was real and bounded, which is exactly why it is
/// easy to leave in place — a tolerance of ±0.002 is wider than anything it has done so far, and
/// the habit of not fixing what has not yet broken is how a repository accumulates a toolchain it
/// cannot reproduce.
/// </para>
/// <para>
/// The version now lives in <c>global.json</c>, which is the mechanism the SDK itself consults, so
/// it governs local builds and CI from one file. The workflows name no version at all, and that
/// absence is the load-bearing part: <c>actions/setup-dotnet</c> installs the version it is given
/// <em>in addition to</em> the one in <c>global.json</c>, so restoring a single
/// <c>dotnet-version</c> line would leave the floating range installed and every local build on
/// the pinned one. Nothing about that failure is visible from the files — the lock is present,
/// the workflow names a version, and the two simply coexist. The first test is the whole point of
/// this file.
/// </para>
/// </remarks>
public partial class SdkPinTests
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

    [GeneratedRegex(@"^\s*dotnet-version\s*:", RegexOptions.Multiline)]
    private static partial Regex DotnetVersionInput();

    /// <summary>
    /// The roll-forward policies that cannot carry a 10.0 SDK into a later feature band or a later
    /// major version. <c>disable</c> is stricter than needed rather than looser, so it is not here.
    /// </summary>
    private static readonly string[] BandPreservingRollForwards =
    [
        "patch",
        "latestPatch",
        "feature",
        "latestFeature",
        "minor",
        "latestMinor",
    ];

    [Fact]
    public void NoWorkflowAsksSetupDotnetForAVersion()
    {
        var offenders = new List<string>();

        foreach (string path in Workflows())
        {
            if (DotnetVersionInput().IsMatch(File.ReadAllText(path)))
                offenders.Add(Path.GetRelativePath(RepositoryRoot, path));
        }

        Assert.True(
            offenders.Count == 0,
            $"dotnet-version is declared in {string.Join(", ", offenders)}. " +
            "setup-dotnet installs a version it is given as well as the one in global.json, so the " +
            "range is installed too and the pin governs local builds only. Delete the input; global.json is the authority.");
    }

    [Fact]
    public void TheSdkVersionIsPinnedToAConcreteNumber()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot, "global.json")));
        JsonElement sdk = document.RootElement.GetProperty("sdk");
        string version = sdk.GetProperty("version").GetString() ?? string.Empty;

        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", version);

        string rollForward = sdk.TryGetProperty("rollForward", out JsonElement policy)
            ? policy.GetString() ?? string.Empty
            : "latestPatch";

        Assert.Contains(rollForward, BandPreservingRollForwards);
    }

    private static IEnumerable<string> Workflows() =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot, ".github", "workflows"), "*.yml");
}
