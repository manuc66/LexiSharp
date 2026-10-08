using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Postgres;
using LexiSharp.Ranking;

// Published and run by CI as `LexiSharp.AotSmoke`. Everything below is a check, not a demo:
// any failure sets the exit code, so a search path that quietly stopped working under Native
// AOT fails the workflow instead of printing something odd.

var failures = new List<string>();

void Check(bool condition, string what)
{
    if (condition)
    {
        Console.WriteLine($"ok   {what}");
    }
    else
    {
        Console.Error.WriteLine($"FAIL {what}");
        failures.Add(what);
    }
}

// --- The core package: index, search, and one read of the statistics the scorer depends on.

var index = new InMemoryTextIndex();
ITextSearchEngine engine = new RankedTextSearchEngine(index, new Bm25Scorer());

engine.Index(new[]
{
    new SearchDocument("1", "The search engine uses BM25 to rank the results"),
    new SearchDocument("2", "TF-IDF is a classic method of textual search"),
    new SearchDocument("3", "Italian cuisine is renowned in Rome"),
});

var hits = engine.Search("textual search");
Check(hits.Count > 0, "core: a two-term query returns hits");
Check(hits.Count > 0 && hits[0].DocumentId == "2", "core: the document holding both terms ranks first");
Check(hits.Count > 0 && hits[0].Document.Text.Length > 0, "core: the hit carries its document");
Check(engine.Search("cuisine").Any(r => r.DocumentId == "3"), "core: a single-term query finds its document");
Check(index.GetStatistics().DocumentCount == 3, "core: the statistics count what was indexed");

// --- The Postgres package: the jsonb read path, which is where the source-generated context
// --- replaced the reflection serializer. Needs a server; skipped without one, exactly as the
// --- integration suite is.

var connection = Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION");

if (connection is null)
{
    Console.WriteLine("skip postgres: POSTGRES_TEST_CONNECTION is not set");
}
else
{
    var table = $"lexisharp_aot_smoke_{Guid.NewGuid().ToString("N")[..8]}";

    using var postgres = new PostgresTextSearchEngine(
        connection, new PostgresIndexOptions { Table = table });

    try
    {
        postgres.Add(new SearchDocument(
            "a", "The quick brown fox jumps over the lazy dog",
            new Dictionary<string, string> { ["kind"] = "fable" }, "fable"));
        postgres.Add(new SearchDocument("b", "A quick fox, a lazy dog."));

        var found = postgres.Search("quick fox", new SearchOptions(Limit: 10));
        Check(found.Count == 2, "postgres: both documents are found");

        var fable = found.FirstOrDefault(r => r.DocumentId == "a");
        Check(fable.DocumentId is not null, "postgres: the document with fields comes back");
        Check(
            fable.Document.Fields is not null
                && fable.Document.Fields.TryGetValue("kind", out var kind)
                && kind == "fable",
            "postgres: the Fields map survives the jsonb round trip");
        Check(fable.Document.Category == "fable", "postgres: the category survives the round trip");
    }
    finally
    {
        postgres.DropSchema();
    }
}

Console.WriteLine(failures.Count == 0
    ? "AOT smoke: every check passed"
    : $"AOT smoke: {failures.Count} check(s) failed");

return failures.Count == 0 ? 0 : 1;
