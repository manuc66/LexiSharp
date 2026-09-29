using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace LexiSharp.Tests;

/// <summary>
/// Emits small managed assemblies at test time, so a comparison tool is tested against real
/// metadata rather than against a checked-in binary nobody can read.
/// </summary>
/// <remarks>
/// <para>
/// The fixtures are never loaded or executed: they exist to be <em>read</em>. Emitting them here
/// rather than committing two .dll files keeps the thing under test visible in a diff — the whole
/// point is that one fixture differs from another by a single added type.
/// </para>
/// <para>
/// Two details exist purely to make the metadata-token trap reproducible, because a fixture that
/// does not reproduce it tests nothing:
/// </para>
/// <list type="number">
/// <item><description>
/// A method takes a parameter whose type is declared <b>in the same assembly</b>, so its signature
/// blob encodes a <c>TypeDef</c> token. A signature of no parameters encodes none, and would be
/// byte-identical however the tables were renumbered.
/// </description></item>
/// <item><description>
/// That parameter's type is declared <b>after</b> the types that <c>extraTypes</c> adds, so adding
/// a type shifts its row and therefore its token.
/// </description></item>
/// </list>
/// </remarks>
internal static class TestAssemblyBuilder
{
    /// <summary>Name of the type the methods under test take as a parameter.</summary>
    public const string ParameterTypeName = "Shared";

    /// <summary>
    /// Builds a managed assembly. The knobs are the differences the comparison tests need to
    /// isolate: <paramref name="extraTypes"/> adds types, <paramref name="nopCount"/> changes a
    /// body length, <paramref name="extraMethods"/> adds methods, <paramref name="localCount"/>
    /// changes a locals signature.
    /// </summary>
    public static byte[] Build(
        string assemblyName = "Fixture",
        int extraTypes = 0,
        int nopCount = 2,
        int extraMethods = 0,
        int localCount = 0,
        bool overloads = false)
    {
        var metadata = new MetadataBuilder();
        var il = new MethodBodyStreamEncoder(new BlobBuilder());

        metadata.AddAssembly(
            metadata.GetOrAddString(assemblyName),
            new Version(1, 0, 0, 0),
            culture: default(StringHandle),
            publicKey: default(BlobHandle),
            AssemblyFlags.PublicKey,
            AssemblyHashAlgorithm.Sha1);

        metadata.AddModule(
            generation: 0,
            metadata.GetOrAddString(assemblyName + ".dll"),
            metadata.GetOrAddGuid(Guid.Parse("2f1c9a44-6d0b-4c2e-9a3f-1b7d5e8c0a44")),
            encId: default(GuidHandle),
            encBaseId: default(GuidHandle));

        StandaloneSignatureHandle locals = BuildLocals(metadata, localCount);
        var ns = metadata.GetOrAddString("Fixture");
        var noParameters = metadata.GetOrAddBlob(new byte[] { 0x00, 0x00, 0x01, 0x00 });

        // The added types come first, on purpose: they shift the row of every type declared after
        // them, and with it the token that type's name carries in a signature.
        for (int i = 0; i < extraTypes; i++)
            AddType(metadata, il, ns, "Added" + i, noParameters, nopCount, locals);

        int sharedRow = AddType(metadata, il, ns, ParameterTypeName, noParameters, nopCount, locals);
        var takesShared = ParameterSignature(metadata, sharedRow);

        AddType(metadata, il, ns, "Subject", takesShared, nopCount, locals, extraMethods, noParameters, overloads);

        var header = new PEHeaderBuilder(
            imageCharacteristics: Characteristics.Dll | Characteristics.ExecutableImage,
            subsystem: Subsystem.WindowsCui);

        // Serialize fills a caller-supplied BlobBuilder and returns only a content id, so the
        // image has to be read back out of the builder it was written into.
        var image = new BlobBuilder();

        new ManagedPEBuilder(header, new MetadataRootBuilder(metadata), il.Builder).Serialize(image);

        return image.ToArray();
    }

    private static StandaloneSignatureHandle BuildLocals(MetadataBuilder metadata, int localCount)
    {
        if (localCount == 0)
            return default;

        var blob = new BlobBuilder();
        blob.WriteByte(0x07);                                     // LOCAL_SIG
        blob.WriteCompressedInteger(localCount);

        for (int i = 0; i < localCount; i++)
            blob.WriteByte(0x08);                                 // ELEMENT_TYPE_I4

        return metadata.AddStandaloneSignature(metadata.GetOrAddBlob(blob));
    }

    /// <summary>
    /// A method signature for <c>void M(Shared)</c>, with <paramref name="sharedRow"/> encoded as
    /// the <c>TypeDefOrRef</c> coded token a real compiler would emit.
    /// </summary>
    private static BlobHandle ParameterSignature(MetadataBuilder metadata, int sharedRow)
    {
        var blob = new BlobBuilder();
        blob.WriteByte(0x00);                                     // DEFAULT calling convention
        blob.WriteCompressedInteger(1);                          // one parameter
        blob.WriteByte(0x01);                                    // VOID return
        blob.WriteByte(0x12);                                    // ELEMENT_TYPE_CLASS
        blob.WriteCompressedInteger(sharedRow << 2);             // TypeDefOrRef, tag 0 = TypeDef
        blob.WriteByte(0x00);                                    // no sentinel parameters
        return metadata.GetOrAddBlob(blob);
    }

    /// <summary>Adds a type and returns its <c>TypeDef</c> row id.</summary>
    private static int AddType(
        MetadataBuilder metadata,
        MethodBodyStreamEncoder il,
        StringHandle ns,
        string name,
        BlobHandle signature,
        int nopCount,
        StandaloneSignatureHandle locals,
        int extraMethods = 0,
        BlobHandle? extraSignature = null,
        bool overloads = false)
    {
        // A TypeDef records the RID of its *first* method, and that row is written before the
        // methods exist, so it is predicted from the table's current row count.
        int firstMethod = metadata.GetRowCount(TableIndex.MethodDef) + 1;
        int row = metadata.GetRowCount(TableIndex.TypeDef) + 1;

        metadata.AddTypeDefinition(
            TypeAttributes.Public | TypeAttributes.Class,
            ns, metadata.GetOrAddString(name),
            baseType: default, fieldList: default, methodList: MetadataTokens.MethodDefinitionHandle(firstMethod));

        // The method under test, then an overload of it under the same name and a different
        // signature, then any extras.
        AddMethod(metadata, il, "Work", signature, nopCount, locals);

        if (overloads && extraSignature is not null)
            AddMethod(metadata, il, "Work", extraSignature.Value, nopCount, locals);

        for (int i = 0; i < extraMethods; i++)
            AddMethod(metadata, il, "Extra" + (i + 1), extraSignature ?? signature, nopCount, locals);

        return row;
    }

    private static void AddMethod(
        MetadataBuilder metadata,
        MethodBodyStreamEncoder il,
        string name,
        BlobHandle signature,
        int nopCount,
        StandaloneSignatureHandle locals)
    {
        var code = new InstructionEncoder(new BlobBuilder(), new ControlFlowBuilder());

        for (int n = 0; n < nopCount; n++)
            code.OpCode(ILOpCode.Nop);

        // A method with locals is emitted in the fat format, whose code is 4-byte aligned, and the
        // RVA the method row carries must be the aligned one.
        if (!locals.IsNil)
            il.Builder.Align(4);

        metadata.AddMethodDefinition(
            MethodAttributes.Public | MethodAttributes.Static,
            MethodImplAttributes.IL,
            metadata.GetOrAddString(name),
            signature,
            bodyOffset: il.Builder.Count,
            parameterList: default);

        il.AddMethodBody(code, maxStack: 8, locals, default);
    }
}
