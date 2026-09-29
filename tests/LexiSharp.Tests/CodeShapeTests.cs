using LexiSharp.CodeShape;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Tests the tool that compares two builds of an assembly for unintended codegen changes.
/// </summary>
/// <remarks>
/// The fixtures are emitted in <see cref="TestAssemblyBuilder"/> rather than committed, so the
/// differences between the two sides of each test are visible in the test source instead of hidden
/// inside a binary. The first two tests are the point of the whole exercise: they pin the two
/// implementations that look correct and are not.
/// </remarks>
public class CodeShapeTests
{
    /// <summary>
    /// Adding a type renumbers the metadata tables, so every signature that names a type carries a
    /// different token afterwards. A comparison keyed on the raw signature blob therefore reports
    /// the affected methods as *gone and replaced* — not as resized — and a real build here
    /// reported 1 182 methods changed after a single record and two enums were added. Keys are
    /// decoded to type names so that nothing appears on either side except what was really added.
    /// </summary>
    [Fact]
    public void AddingAType_ReportsNoChangedMethod()
    {
        var before = Read(TestAssemblyBuilder.Build(extraTypes: 0));
        var after = Read(TestAssemblyBuilder.Build(extraTypes: 3));

        var difference = ShapeComparison.Compare(before, after);

        Assert.True(
            difference.NoCostChange,
            $"adding types must not read as a codegen change, but {difference.ChangedMethods} method(s) were reported");

        // The assertion that pins the trap. A token-bearing key would push every method whose
        // signature names a type into both of these lists, so OnlyLeft is where the damage shows.
        Assert.Empty(difference.OnlyLeft);
        Assert.Equal(3, difference.OnlyRight.Count);
        Assert.All(difference.OnlyRight, key => Assert.Contains("Added", key, StringComparison.Ordinal));
    }

    /// <summary>
    /// The trap is only reproducible if a signature names a type at all: a parameterless signature
    /// encodes no token and would be byte-identical however the tables were renumbered, so a
    /// fixture without one would pass a broken implementation.
    /// </summary>
    [Fact]
    public void TheFixtureCarriesATypeTokenInASignature()
    {
        var shape = Read(TestAssemblyBuilder.Build());

        // Single(predicate) throws unless there is exactly one, which is the precondition.
        var work = shape.Methods.Values
            .Single(method => method.Key.Contains("Subject::Work", StringComparison.Ordinal));

        Assert.Contains(TestAssemblyBuilder.ParameterTypeName, work.Key, StringComparison.Ordinal);
    }

    /// <summary>
    /// A method body that changed size has changed cost, and that is the question the tool exists
    /// to answer. If this test ever fails, the comparison has gone blind.
    /// </summary>
    [Fact]
    public void ChangingABodyLength_IsReported()
    {
        var before = Read(TestAssemblyBuilder.Build(nopCount: 2));
        var after = Read(TestAssemblyBuilder.Build(nopCount: 7));

        var difference = ShapeComparison.Compare(before, after);

        // Both fixture types carry a Work method, so both bodies change length.
        Assert.Equal(2, difference.Resized.Count);
        Assert.Equal(2, difference.ChangedMethods);
        Assert.Empty(difference.Relocalized);
        Assert.All(difference.Resized, key => Assert.Contains("Work", key, StringComparison.Ordinal));
    }

    /// <summary>
    /// Changing a local's type changes the frame the method reserves without moving a single
    /// instruction, so it is invisible to an IL-length comparison on its own.
    /// </summary>
    [Fact]
    public void ChangingTheLocalSignature_IsReported()
    {
        var before = Read(TestAssemblyBuilder.Build(localCount: 1));
        var after = Read(TestAssemblyBuilder.Build(localCount: 3));

        var difference = ShapeComparison.Compare(before, after);

        Assert.NotEmpty(difference.Relocalized);
        Assert.Contains(difference.Relocalized, key => key.Contains("Work", StringComparison.Ordinal));
    }

    /// <summary>
    /// A method that only exists on one side is an ordinary source change, not a codegen change:
    /// it is listed, and it does not count as a changed method, so the tool's exit code stays about
    /// cost and not about the source having moved.
    /// </summary>
    [Fact]
    public void AddingAMethod_IsListedButIsNotAChangedMethod()
    {
        var before = Read(TestAssemblyBuilder.Build(extraMethods: 0));
        var after = Read(TestAssemblyBuilder.Build(extraMethods: 2));

        var difference = ShapeComparison.Compare(before, after);

        Assert.Equal(2, difference.OnlyRight.Count);
        Assert.All(difference.OnlyRight, key => Assert.Contains("Extra", key, StringComparison.Ordinal));
        Assert.Equal(0, difference.ChangedMethods);
        Assert.True(difference.NoCostChange);
    }

    /// <summary>
    /// A manifest is a reviewable diff, so the same assembly must produce byte-identical text on
    /// every read, and the lines must be sorted.
    /// </summary>
    [Fact]
    public void ManifestIsStableAndSorted()
    {
        var first = Write(TestAssemblyBuilder.Build(extraTypes: 2, extraMethods: 1));
        var second = Write(TestAssemblyBuilder.Build(extraTypes: 2, extraMethods: 1));

        Assert.Equal(first, second);

        var keys = first
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#'))
            .Select(line => line.Split('\t')[2])
            .ToList();

        Assert.Equal(keys.OrderBy(key => key, StringComparer.Ordinal), keys);
    }

    /// <summary>
    /// Two methods can share a name and differ only by signature. Keyed on the name alone they
    /// collapse into one entry and one of them silently vanishes from the report, which is worse
    /// than a spurious difference: a method nobody is measuring any more.
    /// </summary>
    [Fact]
    public void OverloadsAreNotCollapsed()
    {
        var shape = Read(TestAssemblyBuilder.Build(overloads: true));

        var work = shape.Methods.Values
            .Where(method => method.Key.Contains("Subject::Work", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, work.Count);
        Assert.Equal(2, work.Select(method => method.Key).Distinct().Count());
        Assert.Contains(work, method => method.Locals == string.Empty || method.Locals.Length > 0);
    }

    /// <summary>Keys must identify a method without depending on anything the compiler renumbers.</summary>
    [Fact]
    public void KeysAreTokenFree()
    {
        var shape = Read(TestAssemblyBuilder.Build(extraTypes: 1));

        // A metadata token is a 4-byte value that shifts whenever a row is inserted. Decoded type
        // names contain dots, angle brackets and backticks instead, so a key carrying a token would
        // look like a bare integer sitting between a return type and a parameter list.
        Assert.All(shape.Methods.Keys, key =>
        {
            Assert.Contains("::", key, StringComparison.Ordinal);
            Assert.Contains("Fixture.", key, StringComparison.Ordinal);
            Assert.DoesNotContain(" 0x", key, StringComparison.Ordinal);
        });
    }

    private static AssemblyShape Read(byte[] assembly) => WithTempFile(assembly, shape => shape);

    private static string Write(byte[] assembly) => WithTempFile(assembly, shape =>
    {
        using var text = new StringWriter();
        shape.Write(text);
        return text.ToString();
    });

    /// <summary>Writes a fixture to disk, hands it to <paramref name="use"/>, and cleans up.</summary>
    private static T WithTempFile<T>(byte[] assembly, Func<AssemblyShape, T> use)
    {
        string path = Path.Combine(Path.GetTempPath(), $"cod-shape-{Guid.NewGuid():N}.dll");
        File.WriteAllBytes(path, assembly);

        try
        {
            return use(AssemblyShape.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
