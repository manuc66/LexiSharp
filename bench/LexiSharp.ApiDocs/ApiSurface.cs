using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LexiSharp.ApiDocs;

/// <summary>The kind of a public type, as the reference page shows it.</summary>
internal enum ApiTypeKind
{
    Interface,
    Record,
    Class,
    RecordStruct,
    Struct,
    Enum,
    Delegate,
}

/// <summary>
/// One public type of a shipped assembly: its name, its kind, the contracts it declares, the
/// first sentence of its own XML summary, and the guide page that covers it.
/// </summary>
/// <param name="Namespace">Declaring namespace, without nesting dots.</param>
/// <param name="Name">The type's name, generic parameters included as <c>&lt;T&gt;</c>.</param>
/// <param name="Kind">Interface, record, class, struct, enum or delegate.</param>
/// <param name="Contracts">
/// The base type and the interfaces the type declares, in that order, as short names.
/// <see cref="object"/>, <see cref="ValueType"/> and <see cref="Enum"/> are left out: they say
/// nothing a reader did not already assume from the kind.
/// </param>
/// <param name="Summary">First paragraph of the XML summary, tags stripped, or empty.</param>
/// <param name="Guide">The guide page covering this type's namespace, or empty when none does.</param>
internal sealed record ApiType(
    string Namespace,
    string Name,
    ApiTypeKind Kind,
    IReadOnlyList<string> Contracts,
    string Summary,
    string Guide);

/// <summary>
/// Reads the public surface of the shipped assemblies, and the summaries their XML
/// documentation carries.
/// </summary>
/// <remarks>
/// <para>
/// The XML file and the assembly are read from the same directory, so the summary of a type
/// always belongs to the type next to it. The XML is a build output rather than a source of
/// truth: a missing or stale one shows up as an empty summary, which is a hole in the page
/// rather than a wrong sentence.
/// </para>
/// <para>
/// Nothing is resolved and nothing is loaded. Contracts are read as the metadata names them —
/// a type from another assembly is a name, not a handle to chase — which is what lets this
/// read a package whose own dependencies are not on disk.
/// </para>
/// </remarks>
internal static class ApiSurface
{
    /// <summary>Types whose name starts with this are compiler-generated and never listed.</summary>
    private const char CompilerGenerated = '<';

    /// <summary>Reads one assembly's public types, in ordinal order by full name.</summary>
    /// <param name="assemblyPath">The managed assembly to read.</param>
    public static IReadOnlyList<ApiType> Read(string assemblyPath)
    {
        var summaries = ReadSummaries(XmlPathFor(assemblyPath));
        var types = new List<ApiType>();

        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var reader = new ShortTypeNames();

        foreach (var handle in metadata.TypeDefinitions)
        {
            var definition = metadata.GetTypeDefinition(handle);
            string name = metadata.GetString(definition.Name);

            if (name.Length == 0 || name[0] == CompilerGenerated || !IsVisible(definition.Attributes))
                continue;

            string ns = NamespaceOf(metadata, definition);
            string baseName = BaseTypeOf(metadata, definition, reader);
            ApiTypeKind kind = KindOf(metadata, definition, baseName, reader);

            // The documentation id carries no generic parameters, and a nested type carries its
            // declaring types: looking a summary up by the name the page displays would miss
            // every one of them.
            types.Add(new ApiType(
                ns,
                NameWith(metadata, definition, name),
                kind,
                ContractsOf(metadata, definition, baseName, kind, reader),
                summaries.GetValueOrDefault($"T:{ns}.{DocumentationNameOf(metadata, definition, name)}", ""),
                GuideFor(ns)));
        }

        return types
            .OrderBy(type => type.Namespace, StringComparer.Ordinal)
            .ThenBy(type => type.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static string XmlPathFor(string assemblyPath)
    {
        string withoutExtension = Path.ChangeExtension(assemblyPath, null)!;
        return withoutExtension + ".xml";
    }

    private static bool IsVisible(TypeAttributes attributes)
    {
        var visibility = attributes & TypeAttributes.VisibilityMask;
        return visibility is TypeAttributes.Public or TypeAttributes.NestedPublic;
    }

    /// <summary>
    /// The type's own name, generic parameters appended, nested types written as
    /// <c>Outer.Inner</c>. <c>Foo&lt;T&gt;</c> in the reference page, so a generic contract is
    /// listed under a name a reader would search for.
    /// </summary>
    private static string NameWith(MetadataReader metadata, TypeDefinition definition, string name)
    {
        var parameters = definition.GetGenericParameters()
            .Select(metadata.GetGenericParameter)
            .Select(parameter => metadata.GetString(parameter.Name))
            .ToList();

        // `LexiSharpHit`1` is the CLR's name for `LexiSharpHit<T>`. The backtick is metadata
        // arity, not part of any name a reader types or searches for.
        int arity = name.IndexOf('`');
        if (arity >= 0)
            name = name[..arity];

        string own = parameters.Count == 0 ? name : $"{name}<{string.Join(", ", parameters)}>";

        if (definition.GetDeclaringType().IsNil)
            return own;

        var declaring = metadata.GetTypeDefinition(definition.GetDeclaringType());
        string outer = NameWith(metadata, declaring, metadata.GetString(declaring.Name));

        return parameters.Count == 0 ? outer + "." + name : outer + "." + own;
    }

    /// <summary>
    /// The name a type carries in a documentation id: the declaring types joined by a dot, with
    /// the arity backtick kept. The compiler writes <c>T:LexiSharp.LexiSharpIndex`1</c>, so a
    /// lookup that drops the backtick finds no summary for any generic type.
    /// </summary>
    private static string DocumentationNameOf(MetadataReader metadata, TypeDefinition definition, string name)
    {
        if (definition.GetDeclaringType().IsNil)
            return name;

        var declaring = metadata.GetTypeDefinition(definition.GetDeclaringType());
        string outer = DocumentationNameOf(metadata, declaring, metadata.GetString(declaring.Name));

        return outer + "." + name;
    }

    private static ApiTypeKind KindOf(
        MetadataReader metadata,
        TypeDefinition definition,
        string baseName,
        ShortTypeNames reader)
    {
        if ((definition.Attributes & TypeAttributes.Interface) != 0)
            return ApiTypeKind.Interface;

        if (baseName is "System.Enum")
            return ApiTypeKind.Enum;

        if (baseName is "System.MulticastDelegate" or "System.Delegate")
            return ApiTypeKind.Delegate;

        if (baseName is "System.ValueType")
            return IsRecordStruct(metadata, definition) ? ApiTypeKind.RecordStruct : ApiTypeKind.Struct;

        return IsRecord(metadata, definition) ? ApiTypeKind.Record : ApiTypeKind.Class;
    }

    /// <summary>
    /// Whether a value type is a <c>record struct</c>, from the <c>PrintMembers</c> method the
    /// compiler adds to one. It has to be a different marker from the class-record one: a record
    /// struct gets neither <c>EqualityContract</c> nor <c>&lt;Clone&gt;$</c>, so looking for
    /// those finds every record and no record struct, and the page then says <c>struct</c> for a
    /// type that has value equality and a <c>with</c> expression.
    /// </summary>
    private static bool IsRecordStruct(MetadataReader metadata, TypeDefinition definition)
    {
        foreach (var handle in definition.GetMethods())
        {
            if (metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "PrintMembers")
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the type is a <c>record</c>, from the member the compiler adds to every one of
    /// them: a protected <c>EqualityContract</c> property, plus the <c>&lt;Clone&gt;$</c> method.
    /// The distinction is worth making in an index — a <c>SearchDocument</c> is constructed, an
    /// <c>InMemoryTextIndex</c> is not — but it is a compiler convention, so it is read from
    /// metadata rather than assumed.
    /// </summary>
    private static bool IsRecord(MetadataReader metadata, TypeDefinition definition)
    {
        foreach (var handle in definition.GetProperties())
        {
            if (metadata.GetString(metadata.GetPropertyDefinition(handle).Name) == "EqualityContract")
                return true;
        }

        foreach (var handle in definition.GetMethods())
        {
            if (metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "<Clone>$")
                return true;
        }

        return false;
    }

    /// <summary>
    /// The namespace a type belongs to, taken from the outermost type that declares it: a
    /// nested type has an empty namespace of its own in metadata, and listing it under one
    /// would put <c>GoldenBaseline.Entry</c> in a group headed by nothing at all.
    /// </summary>
    private static string NamespaceOf(MetadataReader metadata, TypeDefinition definition)
    {
        var current = definition;

        while (!current.GetDeclaringType().IsNil)
            current = metadata.GetTypeDefinition(current.GetDeclaringType());

        return metadata.GetString(current.Namespace);
    }

    private static string BaseTypeOf(MetadataReader metadata, TypeDefinition definition, ShortTypeNames reader)
    {
        if (definition.BaseType.IsNil)
            return "";

        return TypeNameOf(metadata, definition.BaseType, reader);
    }

    private static IReadOnlyList<string> ContractsOf(
        MetadataReader metadata,
        TypeDefinition definition,
        string baseName,
        ApiTypeKind kind,
        ShortTypeNames reader)
    {
        var contracts = new List<string>();

        // The base type first, because it is the one a reader looks for: a reranker that wraps
        // another type behaves differently from one that starts from nothing. What every
        // delegate and every class inherits from says nothing, so it is left out — as is the
        // `delegate` in the Kind column that already says it.
        if (baseName is not ("" or "System.Object" or "System.ValueType" or "System.Enum" or "System.MulticastDelegate" or "System.Delegate"))
            contracts.Add(Shorten(baseName));

        foreach (var handle in definition.GetInterfaceImplementations())
        {
            var implementation = metadata.GetInterfaceImplementation(handle).Interface;

            // An internal interface still shows up in metadata as an implemented one — the four
            // BM25 scorers all implement `IQueryPlannableScorer`, which is not public. The column
            // answers "what do I have to implement", and an internal contract is not an answer.
            if (implementation.Kind == HandleKind.TypeDefinition &&
                !IsVisible(metadata.GetTypeDefinition((TypeDefinitionHandle)implementation).Attributes))
                continue;

            contracts.Add(Shorten(TypeNameOf(metadata, implementation, reader)));
        }

        // `IEquatable<T>` is what the compiler gives every record and record struct: it says the
        // type has value equality, which the Kind column already says, and on this assembly it
        // is 72 rows of the reference saying the same thing twice. Scoped to those two, because
        // a plain class or struct is free to implement it on purpose, and that is a fact about
        // the type.
        if (kind is ApiTypeKind.Record or ApiTypeKind.RecordStruct)
            contracts.Remove("IEquatable");

        return contracts.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToList();
    }

    private static string TypeNameOf(MetadataReader metadata, EntityHandle handle, ShortTypeNames reader) =>
        handle.Kind switch
        {
            HandleKind.TypeDefinition => reader.GetTypeFromDefinition(
                metadata, (TypeDefinitionHandle)handle, rawTypeKind: 0),
            HandleKind.TypeReference => reader.GetTypeFromReference(
                metadata, (TypeReferenceHandle)handle, rawTypeKind: 0),
            _ => reader.GetTypeFromSpecification(metadata, null, (TypeSpecificationHandle)handle, rawTypeKind: 0),
        };

    /// <summary>
    /// Turns a metadata name, or a documentation id, into the name a table cell should show:
    /// no namespace, no <c>T:</c> prefix, no arity backtick, no generic arguments, no parameter
    /// list.
    /// </summary>
    /// <remarks>
    /// The three kinds of input need the same treatment, which is the only reason this is one
    /// method. A metadata name arrives as <c>LexiSharp.Ranking.IScoreExplainer</c> or as
    /// <c>System.IEquatable`1</c>; a documentation id — what a <c>&lt;see cref&gt;</c> holds —
    /// arrives as <c>T:LexiSharp.Ranking.IScoreExplainer</c>, as
    /// <c>T:System.Collections.Concurrent.ConcurrentDictionary`2</c>, or, for a method, as
    /// <c>M:LexiSharp.Benchmarking.CorpusBenchmark.Run(...)</c>. Rendering any of those into a
    /// prose summary reads as a compiler error leaking into a paragraph, which is what it is:
    /// <c>Tuning knobs of a CancellationToken).</c> is a sentence a reader cannot parse.
    /// </remarks>
    private static string Shorten(string name)
    {
        string text = name;

        if (text.Length > 1 && text[1] == ':')
            text = text[2..];

        // A method's id ends in its parameter list, which is a nested list of type names and so
        // can contain dots and backticks of its own. Cut it before anything else is measured,
        // or `Run(CorpusBenchmarkOptions, CancellationToken)` decides the name.
        int parameters = text.IndexOf('(');
        if (parameters >= 0)
            text = text[..parameters];

        int arity = text.IndexOf('`');
        if (arity >= 0)
            text = text[..arity];

        int nested = text.LastIndexOf('+');
        if (nested >= 0)
            text = text[(nested + 1)..];

        int arguments = text.IndexOf('<');
        if (arguments >= 0)
            text = text[..arguments];

        int separator = text.LastIndexOf('.');
        return separator < 0 ? text : text[(separator + 1)..];
    }

    /// <summary>
    /// The guide page for a namespace. Hand-maintained, and deliberately small: it is the only
    /// judgement in the file, and a namespace with no entry is reported by <c>--check</c> rather
    /// than guessed at, because "where should this be documented" is a decision, not a lookup.
    /// </summary>
    private static string GuideFor(string ns) => ns switch
    {
        "LexiSharp" => "getting-started.md",
        "LexiSharp.Benchmarking" => "benchmarks.md",
        "LexiSharp.Classification" => "text-analysis.md",
        "LexiSharp.Core" => "reference.md",
        "LexiSharp.Embeddings" => "embeddings.md",
        "LexiSharp.Expansion" => "embeddings.md",
        "LexiSharp.Highlighting" => "querying.md",
        "LexiSharp.Hybrid" => "pipelines.md",
        "LexiSharp.Indexing" => "indexing.md",
        "LexiSharp.Keywords" => "text-analysis.md",
        "LexiSharp.Linguistics" => "text-analysis.md",
        "LexiSharp.Ranking" => "ranking.md",
        "LexiSharp.Similarity" => "text-analysis.md",
        "LexiSharp.Sources" => "getting-started.md",
        "LexiSharp.AspNetCore" => "getting-started.md",
        "LexiSharp.MessagePack" => "reference.md",
        "LexiSharp.Postgres" or "LexiSharp.ParadeDB" => "backends.md",
        _ => "",
    };

    /// <summary>
    /// Reads the first paragraph of every <c>&lt;summary&gt;</c> in the XML file, keyed by the
    /// documentation id of the type it documents.
    /// </summary>
    private static Dictionary<string, string> ReadSummaries(string path)
    {
        var summaries = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!File.Exists(path))
            return summaries;

        var document = XDocument.Load(path);

        foreach (var member in document.Descendants("member"))
        {
            string? id = member.Attribute("name")?.Value;

            if (id is null || !id.StartsWith("T:", StringComparison.Ordinal))
                continue;

            string summary = FirstParagraph(member.Element("summary"));

            if (summary.Length > 0)
                summaries[id] = summary;
        }

        return summaries;
    }

    /// <summary>
    /// The summary as a reader sees it: the first paragraph, inlined, tags removed, whitespace
    /// collapsed. Tags are dropped rather than rendered — a summary that leans on a table or a
    /// list is a summary written for a page, and only its opening sentence survives here.
    /// </summary>
    private static string FirstParagraph(XElement? summary)
    {
        if (summary is null)
            return "";

        XElement? paragraph = summary.Elements("para").FirstOrDefault();
        string text = Flatten(paragraph ?? summary);

        // `<inheritdoc/>` resolves at compile time, and the resolved text lives in the
        // documentation of the base member, not here: an inheriting type gets no summary rather
        // than a copy of a sentence that may not be about this type.
        return text.Length == 0 ? "" : text;
    }

    private static string Flatten(XElement element)
    {
        var text = new StringBuilder();

        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText value:
                    text.Append(value.Value);
                    break;

                case XElement child when child.Name.LocalName is "inheritdoc":
                    continue;

                case XElement child:
                    FlattenInto(child, text);
                    break;
            }
        }

        return Regex.Replace(text.ToString(), @"\s+", " ").Trim();
    }

    private static void FlattenInto(XElement element, StringBuilder text)
    {
        switch (element.Name.LocalName)
        {
                // A reference to another member, written for an IDE: the reader gets the name, in
            // the place the tag sat. No padding spaces are added, because the text around it
            // already carries the punctuation — `<c>a <see cref="Run"/>.</c>` is the sentence
            // the author wrote, and appending "Run " between the two gives "a Run ." in the page.
            case "see" or "seealso":
                string? reference = element.Attribute("cref")?.Value ?? element.Attribute("langword")?.Value;

                if (reference is not null)
                    text.Append(Shorten(reference));

                return;

            // Every other element — a paragraph, a list, a <c>term</c>, a <b>warning</b> — is
            // flattened to the text it wraps. The tag is dropped and the words kept, because the
            // page is one column wide and a tag would mean nothing there.
            default:
                foreach (var node in element.Nodes())
                {
                    if (node is XText value)
                        text.Append(value.Value);
                    else if (node is XElement nested)
                        FlattenInto(nested, text);
                }

                return;
        }
    }

    /// <summary>
    /// Renders metadata type names with their namespace, for the caller to shorten. The shape of
    /// this is the one <c>cod-shape</c> uses; it lives here too rather than being shared, because
    /// the two want opposite things from a name — a dictionary key needs the full one, a table
    /// cell needs the short one — and a shared abstraction between them would be a flag.
    /// </summary>
    private sealed class ShortTypeNames : ISignatureTypeProvider<string, object?>
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
            return ns.Length == 0 ? reader.GetString(reference.Name) : ns + "." + reader.GetString(reference.Name);
        }

        public string GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawTypeKind) => reader.GetTypeSpecification(handle).DecodeSignature(this, context);

        private static string FullName(MetadataReader reader, TypeDefinition type)
        {
            string declaring = type.GetDeclaringType().IsNil
                ? string.Empty
                : FullName(reader, reader.GetTypeDefinition(type.GetDeclaringType())) + "+";

            string ns = reader.GetString(type.Namespace);
            return declaring + (ns.Length == 0 ? string.Empty : ns + ".") + reader.GetString(type.Name);
        }
    }
}
