using System.Text.Json.Serialization;

namespace LexiSharp.Postgres;

/// <summary>
/// The compile-time JSON metadata for the only two shapes this package writes to and reads back
/// from a <c>jsonb</c> column: a document's <c>Fields</c>/<c>TextFields</c>, serialized as an
/// <see cref="IReadOnlyDictionary{TKey,TValue}"/> and deserialized as a
/// <see cref="Dictionary{TKey,TValue}"/>.
/// </summary>
/// <remarks>
/// <para>
/// The call sites used <c>JsonSerializer.Serialize(value)</c> and
/// <c>JsonSerializer.Deserialize&lt;Dictionary&lt;string, string&gt;&gt;(json)</c>, which are
/// annotated <c>RequiresUnreferencedCode</c> and <c>RequiresDynamicCode</c>: neither can be
/// traced statically, so both break under trimming and Native AOT. With
/// <c>IsAotCompatible</c> on this project they are build failures rather than notes, and one
/// generated context is the fix for all of them.
/// </para>
/// <para>
/// Both declared types are listed because the two directions disagree: the value is written
/// through <c>SearchDocument</c>'s <c>IReadOnlyDictionary&lt;string, string&gt;</c> and read
/// back as a <c>Dictionary&lt;string, string&gt;</c>. A context declaring one and not the other
/// compiles, then fails on the missing converter.
/// </para>
/// </remarks>
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class PostgresJsonContext : JsonSerializerContext;
