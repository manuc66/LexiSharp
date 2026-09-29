using System.Globalization;

namespace LexiSharp.CodeShape;

/// <summary>
/// Reads and compares the structural shape of compiled assemblies.
/// </summary>
/// <remarks>
/// Internal build tooling, not a shipped library. Its purpose is to answer one question — did the
/// generated code change between two builds — without a rebuild and without a human reading a
/// binary diff.
/// </remarks>
internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"cod-shape: {exception.Message}");
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

        return args[0] switch
        {
            "emit" => Emit(args),
            "compare" => Compare(args),
            "report" => Report(args),
            _ => Unknown(args[0]),
        };
    }

    /// <summary>Writes one assembly's shape as a sorted manifest.</summary>
    private static int Emit(string[] args)
    {
        // emit <assembly> <output> [type]
        if (args.Length is < 3 or > 4)
        {
            Usage(Console.Error);
            return 2;
        }

        string assembly = args[1];
        string output = args[2];
        string? typeFilter = args.Length == 4 ? args[3] : null;
        var shape = AssemblyShape.Read(assembly, typeFilter);

        using var writer = output == "-" ? Console.Out : new StreamWriter(output);

        shape.Write(writer);
        Console.Error.WriteLine($"{shape.MethodCount} methods, {shape.IlBytes} IL bytes -> {output}");
        return 0;
    }

    /// <summary>Compares two assemblies and fails if anything about a method changed size or locals.</summary>
    private static int Compare(string[] args)
    {
        if (args.Length is < 3 or > 4)
        {
            Usage(Console.Error);
            return 2;
        }

        var left = AssemblyShape.Read(args[1]);
        var right = AssemblyShape.Read(args[2]);
        var difference = ShapeComparison.Compare(left, right);

        ShapeComparison.Write(difference, Console.Out);

        // Only a size or a local change is a codegen change. Methods appearing or disappearing is
        // ordinary when the source changed, so it is reported and does not fail the run: this tool
        // answers "did the cost of this method move", not "did the source change".
        if (difference.NoCostChange)
        {
            Console.Error.WriteLine($"{left.MethodCount} shared methods, all with the same IL length and locals.");
            return 0;
        }

        Console.Error.WriteLine($"{difference.ChangedMethods} shared method(s) changed cost.");
        return 1;
    }

    /// <summary>Prints a summary of one assembly, for a build log.</summary>
    private static int Report(string[] args)
    {
        if (args.Length is < 2 or > 3)
        {
            Usage(Console.Error);
            return 2;
        }

        var shape = AssemblyShape.Read(args[1], args.Length == 3 ? args[2] : null);

        Console.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"assembly\t{System.IO.Path.GetFileName(shape.SourcePath)}\nmethods\t{shape.MethodCount}\nil-bytes\t{shape.IlBytes}"));

        foreach (var method in shape.Methods.Values.OrderBy(method => method.Key, StringComparer.Ordinal).Take(200))
            Console.Out.WriteLine($"{method.IlLength}\t{method.Locals}\t{method.Key}");

        return 0;
    }

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"cod-shape: unknown command '{verb}'");
        Usage(Console.Error);
        return 2;
    }

    private static void Usage(TextWriter writer)
    {
        writer.WriteLine("""
            usage:
              cod-shape emit   <assembly> <output> [type]     write the manifest (output '-' for stdout)
              cod-shape report <assembly> [type]              summarise an assembly
              cod-shape compare <left-assembly> <right>        exit 1 if any shared method changed cost
            """);
    }
}
