using LexiSharp.Core;
using LexiSharp.Eval;
using LexiSharp.Postgres;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// What keeps the ANN curve's plan column honest.
/// </summary>
/// <remarks>
/// <para>
/// The harness reports, beside every point of the <c>ef_search</c> curve, the scan node PostgreSQL
/// chose — because pgvector's planner hook grows the ANN index's estimated startup cost with
/// <c>ef_search</c>, so on a given corpus a large enough value is answered by a sequential scan and
/// an exact top-k. A recall of 1.0 and a step in the latency are the only other signs of it, and
/// both are read as good news until they are not.
/// </para>
/// <para>
/// For that column to mean anything, the statement the harness explains has to be the statement the
/// engine runs. The two are written separately — the harness does not take the engine's internals —
/// so these tests are the coupling: the SQL text, the vector literal's rounding, and the reading of
/// a plan are all pinned here rather than assumed.
/// </para>
/// </remarks>
public class VectorAnnPlanTests
{
    private static PostgresVectorOptions Options() => new()
    {
        Table = "lexisharp_ann_160",
        Dimension = 3,
        IndexMethod = VectorIndexMethod.Hnsw,
        Distance = VectorDistance.Cosine,
        HnswEfSearch = 160,
    };

    /// <summary>
    /// The harness explains a statement; a search runs another. If they differ, the plan column
    /// describes a query nobody issues, and the failure is invisible in the output — the table still
    /// prints a plan, a recall and a latency, all of them about the wrong statement.
    /// </summary>
    [Fact]
    public void TheStatementTheHarnessExplainsIsTheStatementTheEngineRuns()
    {
        var options = Options();

        // BuildSearchSql needs an engine, and an engine with AutoCreateSchema on would talk to a
        // server. The schema install is off, so the object exists purely to render its SQL.
        using var engine = new PostgresVectorSearchEngine(
            "Host=localhost;Port=1;Username=none;Password=none;Database=none",
            new StubProvider(),
            options with { AutoCreateSchema = false });

        Assert.Equal(
            engine.BuildSearchSql("embedding", string.Empty),
            VectorAnnBenchmark.SearchStatement(options));
    }

    /// <summary>
    /// A metadata filter's fragment slots into the statement immediately after the
    /// <c>IS NOT NULL</c> gate, ahead of the ordering — not at the end, and not on a line of its
    /// own. The harness explains the unfiltered statement, so this pins where a filtered one
    /// differs from it.
    /// </summary>
    [Fact]
    public void AFilteredSearchStillAgreesWithTheHarness()
    {
        var options = Options();
        var filters = PostgresMetadataFilterSql.Build(
            [new MetadataFilter("kind", MetadataFilterOperator.Equal, "fable")]);

        using var engine = new PostgresVectorSearchEngine(
            "Host=localhost;Port=1;Username=none;Password=none;Database=none",
            new StubProvider(),
            options with { AutoCreateSchema = false });

        Assert.Equal(
            engine.BuildSearchSql("embedding", filters.Fragment),
            VectorAnnBenchmark.SearchStatement(options, filters.Fragment));
    }

    /// <summary>
    /// A vector bound to <c>@query::vector</c> is a pgvector literal, and its spelling reaches the
    /// planner as part of the statement's parameter. A different rounding rule is a different text,
    /// so the harness's formatter is pinned against the engine's.
    /// </summary>
    [Fact]
    public void TheVectorLiteralIsSpelledTheWayTheEngineSpellsIt()
    {
        float[] vector = [0.1f, -0f, 1f / 3f, float.Epsilon, -2.5f];

        Assert.Equal(VectorText.Format(vector), VectorAnnBenchmark.VectorLiteral(vector));
    }

    /// <summary>
    /// The two answers the column has to tell apart: an approximate top-k over the index, and an
    /// exact one over the table.
    /// </summary>
    [Fact]
    public void AnHnswIndexScanReadsAsTheIndex()
    {
        Assert.Equal("index", VectorAnnBenchmark.PlanNode(Plan("""
            { "Plan":
              { "Node Type": "Index Scan",
                "Index Name": "lexisharp_ann_160_embedding_hnsw",
                "Plans": [ { "Node Type": "Sort", "Plans": [] } ] } }
            """)));
    }

    [Fact]
    public void ASequentialScanUnderATopNSortReadsAsAScan()
    {
        Assert.Equal("seq scan", VectorAnnBenchmark.PlanNode(Plan("""
            { "Plan":
              { "Node Type": "Limit",
                "Plans": [ { "Node Type": "Sort",
                             "Plans": [ { "Node Type": "Seq Scan",
                                          "Relation Name": "lexisharp_ann_160",
                                          "Plans": [] } ] } ] } }
            """)));
    }

    /// <summary>
    /// A scan of some other index is not an answer this harness can vouch for, and the one case where
    /// guessing "index" would be most wrong: the column's whole purpose is to stop an
    /// unrecognised node from reading as the ANN index.
    /// </summary>
    [Fact]
    public void AnIndexThatIsNotTheAnnOneIsNotTheIndex()
    {
        Assert.Equal("?", VectorAnnBenchmark.PlanNode(Plan("""
            { "Plan":
              { "Node Type": "Index Scan",
                "Index Name": "lexisharp_ann_160_pkey",
                "Plans": [] } }
            """)));
    }

    [Fact]
    public void AnUnreadablePlanIsUnknownRatherThanAFault()
    {
        Assert.Equal("?", VectorAnnBenchmark.PlanNode(Plan("""{ "Plan": { "Node Type": "Gather", "Plans": [] } }""")));
    }

    /// <summary>Wraps a plan fragment in the <c>EXPLAIN (FORMAT JSON)</c> envelope, as returned.</summary>
    private static string Plan(string planFragment) => $"[\n{planFragment}\n]\n";

    private sealed class StubProvider : IEmbeddingProvider
    {
        public int Dimension => 3;

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text, EmbeddingUse use, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReadOnlyMemory<float>>(new float[] { 1f, 0f, 0f });
    }
}
