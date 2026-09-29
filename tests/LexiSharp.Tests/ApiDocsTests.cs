using System.Reflection;
using LexiSharp.ApiDocs;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Tests the tool that generates <c>docs/api.md</c> from the compiled assemblies.
/// </summary>
/// <remarks>
/// The page is committed and gated in CI, so a change that alters it fails the build anyway.
/// These tests cover the failure that gate cannot see: a reader-visible page that renders
/// cleanly and describes the wrong thing, which is what a regression in the reader produces.
/// Each one names a way the output was wrong at some point during its writing.
/// </remarks>
public class ApiDocsTests
{
    /// <summary>The core assembly as the test host sees it, XML documentation and all.</summary>
    private static string CoreAssembly => typeof(Core.SearchDocument).Assembly.Location;

    private static IReadOnlyList<ApiType> Read() => ApiSurface.Read(CoreAssembly);

    /// <summary>
    /// A documentation id in prose is a compiler artefact rendered as a sentence:
    /// <c>T:LexiSharp.Core.ITextSearchEngine</c> came out of one summary, and a method
    /// reference came out with its whole parameter list attached — <c>of a
    /// CancellationToken).</c> The reader shortens both, and the page must show neither.
    /// </summary>
    [Fact]
    public void RenderedSummaries_CarryNoCompilerArtefacts()
    {
        string page = ApiReference.Render([("LexiSharp", Read())]);

        Assert.DoesNotContain("T:LexiSharp", page, StringComparison.Ordinal);
        Assert.DoesNotContain("M:LexiSharp", page, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationToken)", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<c>", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<see", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<summary>", page, StringComparison.Ordinal);

        // The arity backtick: `LexiSharpHit`1` is the CLR's name, `LexiSharpHit<T>` is the one
        // a reader types. It appeared on every generic type until the name was stripped.
        Assert.DoesNotContain("`1", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row per public type, and exactly one. The page's only claim is that it indexes the
    /// surface, so a missing row is a hole and a duplicate row is a page that cannot be diffed:
    /// adding a type would show as a change in the wrong place. Counting rows in the whole page
    /// is not enough on its own — a type listed in both tables would balance against one listed
    /// in neither — so each type is looked for by name.
    /// </summary>
    [Fact]
    public void EveryPublicType_AppearsExactlyOnce()
    {
        var types = Read();
        Assert.NotEmpty(types);
        string page = ApiReference.Render([("LexiSharp", types)]);

        foreach (var type in types)
        {
            // Anchored to the start of a row, so a type named in another row's Contracts or
            // What-it-is column is not counted as a row of its own.
            int occurrences = System.Text.RegularExpressions.Regex.Matches(
                page,
                $@"^\| `{System.Text.RegularExpressions.Regex.Escape(type.Name)}` \|",
                System.Text.RegularExpressions.RegexOptions.Multiline).Count;

            Assert.True(occurrences == 1, $"{type.Name} appears in {occurrences} row(s), expected exactly one");
        }
    }

    /// <summary>
    /// Every public type carries the summary its author wrote. An empty one means either that
    /// the reader missed it — the lookup key and the displayed name diverging, which is what
    /// generic and nested types used to do — or that the type is undocumented, which is a hole
    /// in a page that claims to be an index of the surface.
    /// </summary>
    [Fact]
    public void EveryPublicType_CarriesASummary()
    {
        var undocumented = Read()
            .Where(type => type.Summary.Length == 0)
            .Select(type => $"{type.Namespace}.{type.Name}")
            .ToList();

        Assert.Empty(undocumented);
    }

    /// <summary>
    /// The type a nested one is listed under: <c>Declares.Child</c>, grouped under the
    /// namespace of <c>Declares</c>. A bare <c>Entry</c> in a group headed by nothing is what a
    /// nested type used to render as, because it has an empty namespace of its own in metadata.
    /// </summary>
    /// <remarks>
    /// The fixture is a nested type in this very file rather than one in the library. It was
    /// originally <c>GoldenBaseline.Entry</c>, the only nested public type the core had, and the
    /// test then failed the day that type was made internal — a test pinned to whatever the
    /// library happens to contain, rather than to the property it is about.
    /// </remarks>
    [Fact]
    public void ANestedType_IsNamedAndGroupedWithItsDeclaringType()
    {
        var nested = ApiSurface
            .Read(typeof(ApiDocsTests).Assembly.Location)
            .Where(type => type.Name == "ApiDocsTests.Declares.Child")
            .ToList();

        var fixture = Assert.Single(nested);
        Assert.Equal("LexiSharp.Tests", fixture.Namespace);
        Assert.Equal(ApiTypeKind.Record, fixture.Kind);
    }

    /// <summary>
    /// The rendered page is a pure function of the assemblies: same bytes every run, no machine
    /// path, LF endings whatever the platform. This is what lets CI compare it byte for byte, and
    /// it is the only property that makes the gate affordable.
    /// </summary>
    [Fact]
    public void Render_IsRepeatableAndCarriesNoEnvironment()
    {
        string first = ApiReference.Render([("LexiSharp", Read())]);
        string second = ApiReference.Render([("LexiSharp", Read())]);

        Assert.Equal(first, second);
        Assert.DoesNotContain("\r", first, StringComparison.Ordinal);

        // The build directory this ran from, which is the one thing a generated page must never
        // carry: it differs between a contributor's machine and the runner, so a page holding it
        // could never be compared against anything. Checked as a path rather than as "/home/",
        // because the runner is not this machine.
        string buildDirectory = Path.GetDirectoryName(CoreAssembly)!;
        Assert.DoesNotContain(buildDirectory, first, StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.GetCurrentDirectory(), first, StringComparison.Ordinal);
    }

    /// <summary>
    /// A table row is four columns of prose and one separator each. A summary holding a raw
    /// pipe would end the cell early and shift every column after it, which renders as a table
    /// that is subtly wrong rather than one that fails.
    /// </summary>
    [Fact]
    public void EveryTableRow_HasItsFullSetOfColumns()
    {
        var rows = Read();
        string page = ApiReference.Render([("LexiSharp", rows)]);

        foreach (string line in page.Split('\n').Where(line => line.StartsWith("| ", StringComparison.Ordinal)))
        {
            Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(line, @"(?<!\\)\|").Count);
        }
    }

    /// <summary>
    /// A <c>record struct</c> is not a <c>struct</c>: it has value equality, a <c>with</c>
    /// expression and a compiler-added <c>IEquatable</c>, none of which a plain struct has. The
    /// page calls all five of the assembly's public record structs <c>struct</c> until the reader
    /// looks for the member the compiler gives a record struct — <c>PrintMembers</c> — because
    /// the markers that identify a class record (<c>EqualityContract</c>, <c>&lt;Clone&gt;$</c>)
    /// do not exist on it at all.
    /// </summary>
    [Fact]
    public void ARecordStruct_IsNotLabelledAStruct()
    {
        var types = Read();
        var records = types.Where(type => type.Kind == ApiTypeKind.RecordStruct).ToList();

        // A precondition, not the assertion: if the assembly ever drops its record structs, the
        // property below stops being exercised and this is what says so.
        Assert.NotEmpty(records);

        // The compiler adds IEquatable to a record struct, and the reader is what has to know
        // that — otherwise the row contradicts its own Kind column.
        Assert.All(records, type => Assert.DoesNotContain("IEquatable", type.Contracts));

        // And the other direction: IEquatable on anything else is a decision somebody made, so
        // the reader must not have filtered it away.
        Assert.All(
            types.Where(type => type.Contracts.Contains("IEquatable", StringComparer.Ordinal)),
            type => Assert.True(type.Kind is ApiTypeKind.Record or ApiTypeKind.RecordStruct));
    }

    /// <summary>A public type nested in a public type, so the reader has one to be wrong about.</summary>
    public sealed record Declares
    {
        /// <summary>The nested type, which metadata gives an empty namespace of its own.</summary>
        public sealed record Child;
    }
}
