using LexiSharp.Core;
using LexiSharp.Postgres;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// One contract, every SQL engine: <see cref="SearchOptions.ExcludedDocumentIds"/> has to mean the
/// same thing on a Postgres backend as it does on an in-memory one.
/// </summary>
/// <remarks>
/// <para>
/// It did not. Four of the five engines never read the option — <c>PostgresTextSearchEngine</c>,
/// <c>PostgresSparseSearchEngine</c>,
/// <c>PostgresFuzzySearchEngine</c> and <c>PostgresVectorSearchEngine</c> — so a caller that
/// excluded the query's own document got it back, and got it first. The fifth,
/// <c>ParadeDBTextSearchEngine</c>, read it but fetched exactly <c>Options.Window</c> rows and then
/// dropped the excluded ones from that prefix, so its page came back one result short. The second is
/// the quieter failure: the page is well formed, the ranking is right, and a caller asking for ten
/// results receives nine.
/// </para>
/// <para>
/// These run against a real server and auto-skip without <c>POSTGRES_TEST_CONNECTION</c>. The
/// arithmetic behind the short page is unit-tested in <see cref="PostgresDocumentExclusionTests"/>,
/// which needs no server; what these add is the wiring, which is where four of the five were wrong.
/// </para>
/// </remarks>
public class PostgresExcludedDocumentIdsTests
{
    private const string Query = "shared common term";

    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION");

    /// <summary>
    /// Four documents whose every term is shared, so all of them match and the order between them is
    /// decided by the tie-break alone. That is what makes the page-length assertion meaningful: with
    /// content that separated them, a short page could always be explained by the corpus running out
    /// of matches.
    /// </summary>
    private static IReadOnlyList<SearchDocument> Corpus() =>
        new[] { "d1", "d2", "d3", "d4" }
            .Select(id => new SearchDocument(id, Query))
            .ToArray();

    /// <summary>
    /// The contract: the excluded id is gone <em>and</em> the page is still the size that was asked
    /// for. The second half is the half that was broken on ParadeDB and absent everywhere else.
    /// </summary>
    /// <param name="search">Runs the search the same way the caller would.</param>
    private static void AssertExclusion(Func<SearchOptions, IReadOnlyList<SearchResult>> search)
    {
        var all = search(new SearchOptions(Limit: 10));
        Assert.Equal(4, all.Count);

        string top = all[0].DocumentId;
        var after = search(new SearchOptions(
            Limit: 10, ExcludedDocumentIds: new HashSet<string>(StringComparer.Ordinal) { top }));

        Assert.DoesNotContain(after, r => r.DocumentId == top);

        // A page-length assertion rather than a count: an engine that filtered the page after ranking
        // would also satisfy "does not contain", and would be wrong.
        Assert.Equal(3, after.Count);
    }

    private static string Table(string suffix) => $"lexisharp_excl_{suffix}_{Guid.NewGuid():N}"[..44];

    [SkippableFact]
    public void TextEngine_ExcludesTheDocumentAndStillFillsThePage()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        using var engine = new PostgresTextSearchEngine(
            ConnectionString!, new PostgresIndexOptions { Table = Table("text") });

        try
        {
            foreach (var document in Corpus()) engine.Add(document);

            AssertExclusion(options => engine.Search(Query, options));
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void FuzzyEngine_ExcludesTheDocumentAndStillFillsThePage()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        using var engine = new PostgresFuzzySearchEngine(
            ConnectionString!, new PostgresFuzzyOptions { Table = Table("fuzzy") });

        try
        {
            foreach (var document in Corpus()) engine.Add(document);

            AssertExclusion(options => engine.Search(Query, options));
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void SparseEngine_ExcludesTheDocumentAndStillFillsThePage()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        // Every document gets the same sparse vector, so all four match and the ranking between them
        // is the tie-break — the condition the page-length assertion needs.
        var vectors = Corpus().ToDictionary(
            document => document.Text,
            _ => (IReadOnlyDictionary<string, float>)new Dictionary<string, float> { ["shared"] = 1f, ["common"] = 1f, ["term"] = 1f });

        using var engine = new PostgresSparseSearchEngine(
            ConnectionString!,
            new ConstantSparseProvider(vectors),
            new PostgresSparseOptions { Table = Table("sparse") });

        try
        {
            foreach (var document in Corpus()) engine.Add(document);

            AssertExclusion(options => engine.Search(Query, options));
        }
        finally
        {
            engine.DropSchema();
        }
    }

    /// <summary>Hands back the vector registered for a text, so a sparse document scores as expected.</summary>
    private sealed class ConstantSparseProvider(IReadOnlyDictionary<string, IReadOnlyDictionary<string, float>> vectors)
        : ISparseEmbeddingProvider
    {
        public Task<IReadOnlyDictionary<string, float>> GetSparseEmbeddingAsync(
            string text, EmbeddingUse use, CancellationToken cancellationToken = default)
            => Task.FromResult(vectors.TryGetValue(text, out var vector)
                ? vector
                : throw new KeyNotFoundException($"No sparse embedding configured for '{text}'."));
    }
}
