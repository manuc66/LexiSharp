using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace LexiSharp.CodeShape;

/// <summary>
/// The three figures about a method that decide its cost: how long its body is, what its locals
/// are, and a key that identifies it without depending on anything the compiler is free to
/// renumber.
/// </summary>
/// <param name="IlLength">
/// Size of the method body in bytes. Two bodies of the same size and the same locals execute the
/// same instructions against the same stack shape, so they cost the same.
/// </param>
/// <param name="Locals">The decoded local variable types, comma separated.</param>
/// <param name="Key">
/// Declaring type, method name and decoded signature. Deliberately free of metadata tokens —
/// see the remarks on <see cref="AssemblyShape"/>.
/// </param>
internal sealed record MethodShape(int IlLength, string Locals, string Key);

/// <summary>
/// The structural shape of a compiled assembly: what every method is made of, and nothing about
/// which metadata token happens to name it.
/// </summary>
/// <remarks>
/// <para>
/// The whole point of this type is to be a *token-free* description of an assembly, so that two
/// builds of the same source can be compared for codegen changes. Two traps make the obvious
/// implementations wrong in ways that look like findings:
/// </para>
/// <para>
/// <b>Do not key on a raw signature blob.</b> A signature encodes metadata tokens, and adding one
/// class to an assembly renumbers the type table, so every stored signature in the assembly
/// changes with it. Keyed on the blob, adding a single record reports 1 182 changed methods on a
/// build that gained a record and two enums.
/// </para>
/// <para>
/// <b>Do not key on the method name alone.</b> An assembly holds several hundred methods called
/// <c>.ctor</c> or <c>Add</c>; a bare name keeps one entry per name and the survivors differ by
/// luck of declaration order, which is noise that reads as change.
/// </para>
/// <para>
/// So keys are built from decoded type names, and the comparison deliberately looks only at sizes
/// and local types. It is therefore <b>blind to operand values</b>: a call retargeted to a
/// different method of the same signature, without moving a single instruction boundary, is not
/// reported. That is a real limit, and it is why a source diff is checked alongside this and not
/// instead of it.
/// </para>
/// </remarks>
internal sealed class AssemblyShape
{
    public AssemblyShape(string sourcePath, string assemblyName, IReadOnlyDictionary<string, MethodShape> methods)
    {
        SourcePath = sourcePath;
        AssemblyName = assemblyName;
        Methods = methods;
    }

    /// <summary>
    /// Where the assembly was read from. Never keyed on, and never written to a manifest: it is a
    /// local path, it differs between two reads of identical bytes, and it would leak a checkout
    /// directory into a published artifact.
    /// </summary>
    public string SourcePath { get; }

    /// <summary>The assembly's own name, read from metadata. Stable, and meaningful in a diff.</summary>
    public string AssemblyName { get; }

    public IReadOnlyDictionary<string, MethodShape> Methods { get; }

    public int MethodCount => Methods.Count;

    public long IlBytes => Methods.Values.Sum(method => (long)method.IlLength);

    /// <summary>Reads an assembly's shape from disk.</summary>
    /// <param name="path">The managed assembly to read.</param>
    /// <param name="typeFilter">
    /// When set, only types whose name (not namespace) equals this are read. Comparing one type
    /// keeps the output readable when the question is about a single hot class.
    /// </param>
    public static AssemblyShape Read(string path, string? typeFilter = null)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var provider = new TypeNameProvider();

        var methods = new Dictionary<string, MethodShape>(StringComparer.Ordinal);

        foreach (var handle in metadata.MethodDefinitions)
        {
            var definition = metadata.GetMethodDefinition(handle);
            var declaringType = FullName(metadata, metadata.GetTypeDefinition(definition.GetDeclaringType()));
            string methodName = metadata.GetString(definition.Name);

            if (typeFilter is not null &&
                !declaringType.Split('.').Contains(typeFilter, StringComparer.Ordinal))
                continue;

            byte[] il = Array.Empty<byte>();
            string locals = "";

            if (definition.RelativeVirtualAddress != 0)
            {
                var body = pe.GetMethodBody(definition.RelativeVirtualAddress);
                il = body.GetILBytes() ?? Array.Empty<byte>();

                // The locals decide how much frame the method reserves and what is in it, so a
                // change here changes cost even when not one opcode moved.
                if (!body.LocalSignature.IsNil)
                    locals = string.Join(",", metadata.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(provider, null));
            }

            // Decode the signature into its parts: formatting the MethodSignature struct gives its
            // type name, which would collapse every overload onto one key exactly as a bare name does.
            var signature = definition.DecodeSignature(provider, null);
            string key = $"{declaringType}::{methodName}({signature.ReturnType})[{string.Join(",", signature.ParameterTypes)}]";

            methods[key] = new MethodShape(il.Length, locals, key);
        }

        return new AssemblyShape(path, AssemblyNameOf(metadata), methods);
    }

    private static string AssemblyNameOf(MetadataReader metadata)
    {
        if (!metadata.IsAssembly)
            return "<module>";

        return metadata.GetString(metadata.GetAssemblyDefinition().Name);
    }

    /// <summary>
    /// Writes the shape as sorted, tab-separated lines. Line-oriented on purpose: two manifests
    /// diff with <c>diff</c> and review in a pull request, where two JSON documents do not.
    /// </summary>
    public void Write(TextWriter writer)
    {
        writer.WriteLine("# cod-shape 1");
        writer.WriteLine($"# assembly\t{AssemblyName}");
        writer.WriteLine($"# methods\t{MethodCount}");
        writer.WriteLine($"# il-bytes\t{IlBytes}");

        foreach (var method in Methods.Values.OrderBy(method => method.Key, StringComparer.Ordinal))
            writer.WriteLine($"{method.IlLength}\t{method.Locals}\t{method.Key}");
    }

    private static string FullName(MetadataReader metadata, TypeDefinition type)
    {
        string declaring = type.GetDeclaringType().IsNil
            ? string.Empty
            : FullName(metadata, metadata.GetTypeDefinition(type.GetDeclaringType())) + "+";

        string ns = metadata.GetString(type.Namespace);
        return declaring + (ns.Length == 0 ? string.Empty : ns + ".") + metadata.GetString(type.Name);
    }

    /// <summary>Renders metadata types as readable names, so a signature can go in a dictionary key.</summary>
    private sealed class TypeNameProvider : ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";

        public string GetByReferenceType(string elementType) => elementType + "&";

        public string GetFunctionPointerType(MethodSignature<string> signature) => "method*";

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> arguments) => genericType + "<" + string.Join(",", arguments) + ">";

        public string GetGenericMethodParameter(object? context, int index) => "!!" + index;

        public string GetGenericTypeParameter(object? context, int index) => "!" + index;

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetPinnedType(string elementType) => elementType + " pinned";

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => FullName(reader, reader.GetTypeDefinition(handle));

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var reference = reader.GetTypeReference(handle);
            string ns = reader.GetString(reference.Namespace);
            return (ns.Length == 0 ? string.Empty : ns + ".") + reader.GetString(reference.Name);
        }

        public string GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawTypeKind) => reader.GetTypeSpecification(handle).DecodeSignature(this, context);
    }
}
