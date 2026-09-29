namespace LexiSharp.CodeShape;

/// <summary>What changed between two <see cref="AssemblyShape"/> readings.</summary>
/// <param name="Resized">Methods present in both, with a different IL length.</param>
/// <param name="Relocalized">Methods present in both, with a different local signature.</param>
/// <param name="OnlyLeft">Methods only the left assembly has: added on the right, or removed.</param>
/// <param name="OnlyRight">Methods only the right assembly has.</param>
internal sealed record ShapeDifference(
    IReadOnlyList<string> Resized,
    IReadOnlyList<string> Relocalized,
    IReadOnlyList<string> OnlyLeft,
    IReadOnlyList<string> OnlyRight)
{
    /// <summary>
    /// Whether any method present in both assemblies changed size or locals. This is the question
    /// the tool exists to answer, and it is deliberately <b>not</b> "did anything change": a type
    /// or a method appearing on one side is an ordinary source change that says nothing about
    /// cost, and conflating the two would make every source edit read as a codegen event.
    /// </summary>
    public bool NoCostChange => Resized.Count == 0 && Relocalized.Count == 0;

    public int ChangedMethods => Resized.Count + Relocalized.Count;
}

/// <summary>Compares two assembly shapes.</summary>
internal static class ShapeComparison
{
    /// <summary>
    /// Compares two readings, reporting only what a method's <em>size</em> and <em>locals</em> can
    /// settle. Keys are already token-free, so a build that only gained a type reports nothing.
    /// </summary>
    public static ShapeDifference Compare(AssemblyShape left, AssemblyShape right)
    {
        var shared = left.Methods.Keys.Intersect(right.Methods.Keys, StringComparer.Ordinal);

        var resized = shared
            .Where(key => left.Methods[key].IlLength != right.Methods[key].IlLength)
            .OrderBy(key => key, StringComparer.Ordinal).ToList();

        var relocalized = shared
            .Where(key => !string.Equals(left.Methods[key].Locals, right.Methods[key].Locals, StringComparison.Ordinal))
            .OrderBy(key => key, StringComparer.Ordinal).ToList();

        var onlyLeft = left.Methods.Keys.Except(right.Methods.Keys, StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal).ToList();

        var onlyRight = right.Methods.Keys.Except(left.Methods.Keys, StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal).ToList();

        return new ShapeDifference(resized, relocalized, onlyLeft, onlyRight);
    }

    /// <summary>Writes a difference as lines, for a console or a log.</summary>
    public static void Write(ShapeDifference difference, TextWriter writer)
    {
        if (difference.NoCostChange)
            writer.WriteLine("no shared method changed size or locals");

        if (difference.Resized.Count > 0)
        {
            writer.WriteLine($"{difference.Resized.Count} method(s) present in both, different IL length:");

            foreach (var key in difference.Resized)
                writer.WriteLine($"  ~ {key}");
        }

        if (difference.Relocalized.Count > 0)
        {
            writer.WriteLine($"{difference.Relocalized.Count} method(s) present in both, different local signature:");

            foreach (var key in difference.Relocalized)
                writer.WriteLine($"  ~ {key}");
        }

        if (difference.OnlyLeft.Count > 0)
        {
            writer.WriteLine($"{difference.OnlyLeft.Count} method(s) only in the first assembly:");

            foreach (var key in difference.OnlyLeft)
                writer.WriteLine($"  - {key}");
        }

        if (difference.OnlyRight.Count > 0)
        {
            writer.WriteLine($"{difference.OnlyRight.Count} method(s) only in the second assembly:");

            foreach (var key in difference.OnlyRight)
                writer.WriteLine($"  + {key}");
        }
    }
}
