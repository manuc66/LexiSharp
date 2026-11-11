using System.Reflection;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Keeps the 1.0 public API from losing a member by accident.
/// </summary>
/// <remarks>
/// <para>
/// The generated API page (<c>docs/api.md</c>) gates the list of public types. It does not see a
/// constructor, method or property removed from a type that is still listed, which is exactly the
/// shape of a binary-breaking change that a 1.0 release must not ship by accident. This test reads
/// the shipped assemblies and compares their public members with a committed baseline.
/// </para>
/// <para>
/// Additions are allowed: a minor release may add a member without touching the baseline, and the
/// baseline is deliberately a floor rather than an exact snapshot so every additive change does
/// not become a file edit. A removal or a signature change is reported by name. To re-record the
/// floor after a deliberate major release, run the test with <c>LEXISHARP_WRITE_PUBLIC_API=1</c>;
/// re-recording is the one action that makes the test stop catching a removal, so review the diff
/// before committing it.
/// </para>
/// </remarks>
public class PublicApiBaselineTests
{
    /// <summary>The five shipped libraries. The CLI is a tool, not a library surface.</summary>
    private static readonly string[] ShippedAssemblies =
    [
        "LexiSharp",
        "LexiSharp.AspNetCore",
        "LexiSharp.MessagePack",
        "LexiSharp.Postgres",
        "LexiSharp.Onnx",
    ];

    private const string BaselineRelativePath = "tests/LexiSharp.Tests/PublicApiBaseline.txt";

    [Fact]
    public void NoPublicApiMemberRemovedSinceTheBaseline()
    {
        string root = RepositoryRoot();
        string path = Path.Combine(root, BaselineRelativePath);

        List<string> current = CurrentApi();

        if (Environment.GetEnvironmentVariable("LEXISHARP_WRITE_PUBLIC_API") == "1")
        {
            File.WriteAllLines(path, current);
            return;
        }

        Assert.True(File.Exists(path), $"Missing public API baseline: {path}");

        var baseline = File.ReadAllLines(path)
            .Where(line => !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

        var missing = current
            .Where(line => !baseline.Contains(line))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"The public API lost {missing.Count} member(s) since the baseline:{Environment.NewLine}" +
            string.Join(Environment.NewLine, missing.Select(line => "  - " + line)) +
            $"{Environment.NewLine}Additions are allowed; a removal or signature change is a breaking change." +
            $" For a deliberate major release, re-record with LEXISHARP_WRITE_PUBLIC_API=1.");
    }

    private static List<string> CurrentApi()
    {
        var lines = new List<string>();

        foreach (string name in ShippedAssemblies)
        {
            Assembly assembly = Assembly.Load(new AssemblyName(name));
            lines.AddRange(Dump(assembly));
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static IEnumerable<string> Dump(Assembly assembly)
    {
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (Type type in assembly.GetTypes()
                     .Where(t => t.IsPublic || t.IsNestedPublic)
                     .Where(t => !t.Name.StartsWith('<'))
                     .OrderBy(t => TypeName(t), StringComparer.Ordinal))
        {
            string typeName = TypeName(type);
            string kind = type.IsEnum ? "enum"
                : type.IsInterface ? "interface"
                : type.IsValueType ? "struct"
                : "class";

            string baseType = type.BaseType is null
                              || type.BaseType == typeof(object)
                              || type.BaseType == typeof(ValueType)
                              || type.BaseType == typeof(Enum)
                ? ""
                : TypeName(type.BaseType);

            string interfaces = string.Join(
                ",",
                type.GetInterfaces().Where(i => i.IsPublic).Select(TypeName).Order(StringComparer.Ordinal));

            yield return $"T|{typeName}|{kind}|{baseType}|{interfaces}";

            foreach (ConstructorInfo constructor in type.GetConstructors(Declared)
                         .OrderBy(c => c.ToString(), StringComparer.Ordinal))
                yield return $"C|{typeName}|{constructor}";

            foreach (MethodInfo method in type.GetMethods(Declared)
                         .Where(m => !m.IsSpecialName && !m.Name.StartsWith('<'))
                         .OrderBy(m => m.ToString(), StringComparer.Ordinal))
                yield return $"M|{typeName}|{method}";

            foreach (PropertyInfo property in type.GetProperties(Declared).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                string accessors = (property.GetMethod?.IsPublic == true ? "get" : "")
                    + (property.SetMethod?.IsPublic == true ? "set" : "");
                yield return $"P|{typeName}|{property.Name}|{TypeName(property.PropertyType)}|{accessors}";
            }

            foreach (FieldInfo field in type.GetFields(Declared)
                         .Where(f => !f.IsSpecialName)
                         .OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                string constant = field.IsLiteral ? "|" + field.GetRawConstantValue() : "";
                yield return $"F|{typeName}|{field.Name}|{TypeName(field.FieldType)}|{(field.IsStatic ? "static" : "instance")}{constant}";
            }

            foreach (EventInfo @event in type.GetEvents(Declared).OrderBy(e => e.Name, StringComparer.Ordinal))
                yield return $"E|{typeName}|{@event.Name}|{TypeName(@event.EventHandlerType!)}";
        }
    }

    private static string TypeName(Type type)
    {
        if (type.IsByRef)
            return TypeName(type.GetElementType()!) + "&";

        if (type.IsArray)
            return TypeName(type.GetElementType()!) + "[]";

        if (type.IsGenericType)
        {
            string name = type.GetGenericTypeDefinition().FullName!;
            int tick = name.IndexOf('`');
            if (tick >= 0)
                name = name[..tick];

            return name + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
        }

        return type.FullName ?? type.Name;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LexiSharp.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
