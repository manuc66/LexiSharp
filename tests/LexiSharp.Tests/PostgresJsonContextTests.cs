using System.Text.Json;
using LexiSharp.Postgres;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The Postgres engines read and write a document's <c>Fields</c> through
/// <c>PostgresJsonContext</c>. These pin what the source generator was hired to preserve: the
/// bytes a reader sees, and a round trip.
/// </summary>
public class PostgresJsonContextTests
{
    [Fact]
    public void GeneratedContext_WritesTheJsonTheReflectionSerializerWrote()
    {
        IReadOnlyDictionary<string, string> fields = new Dictionary<string, string>
        {
            ["kind"] = "fable",
            ["level"] = "3",
        };

        string generated = JsonSerializer.Serialize(
            fields, PostgresJsonContext.Default.IReadOnlyDictionaryStringString);

        // The pre-source-generation path, which is what every row written by an earlier build
        // contains. jsonb normalises on read, but a caller with its own client sees these bytes.
        Assert.Equal(JsonSerializer.Serialize(fields), generated);
    }

    [Fact]
    public void GeneratedContext_ReadsBackAWrittenFieldsMap()
    {
        IReadOnlyDictionary<string, string> fields = new Dictionary<string, string>
        {
            ["kind"] = "fable",
            ["narrator"] = "omniscient",
        };

        string written = JsonSerializer.Serialize(
            fields, PostgresJsonContext.Default.IReadOnlyDictionaryStringString);

        IReadOnlyDictionary<string, string>? read = JsonSerializer.Deserialize<Dictionary<string, string>>(
            written, PostgresJsonContext.Default.DictionaryStringString);

        Assert.NotNull(read);
        Assert.Equal("fable", read!["kind"]);
        Assert.Equal("omniscient", read["narrator"]);
    }
}
