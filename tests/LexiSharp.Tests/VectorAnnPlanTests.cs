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

    /// <summary>
    /// Every column of a row sits under the heading that names it. A width added to one and not the
    /// other does not fail a run — it prints a table whose numbers no longer line up with their
    /// labels, which is exactly the reader error the plan column was added to prevent.
    /// </summary>
    [Fact]
    public void APointsColumnsSitUnderTheHeadingThatNamesThem()
    {
        string heading = VectorAnnBenchmark.Heading(10);
        string row = new VectorAnnBenchmark.AnnPoint(10, 0.7579, 1.08, "index").ToString();

        Assert.Equal(heading.Length, row.Length);

        // The three numbers are right-aligned in their column, so a column agrees when it ends in
        // the same place.
        AssertRightEdgesAgree(heading, "ef_search", row, "10");
        AssertRightEdgesAgree(heading, "recall@10", row, "0.7579");
        AssertRightEdgesAgree(heading, "latency", row, "1.08 ms");

        // The plan is a word, so it starts under its heading rather than ending under it.
        AssertLeftEdgesAgree(heading, "plan", row, "index");
    }

    /// <summary>
    /// A plan name longer than its column widens the row rather than being cut. Truncating the node
    /// would be the wrong repair: the column's whole purpose is to name what answered, and a
    /// truncated name reads as one of the names it was cut from. Everything from the recall column on
    /// is unaffected, so a widened row still reads as columns.
    /// </summary>
    [Fact]
    public void AnOverlongPlanNameIsPrintedInFullAndWidensOnlyItsOwnColumn()
    {
        const string Name = "a-plan-node-name-longer-than-ten-characters";
        string sized = new VectorAnnBenchmark.AnnPoint(10, 0.7579, 1.08, "index").ToString();
        string widened = new VectorAnnBenchmark.AnnPoint(10, 0.7579, 1.08, Name).ToString();

        Assert.Contains(Name, widened, StringComparison.Ordinal);
        Assert.True(widened.Length > sized.Length);
        Assert.EndsWith(
            sized[sized.IndexOf("0.7579", StringComparison.Ordinal)..],
            widened,
            StringComparison.Ordinal);
    }

    private static void AssertLeftEdgesAgree(string left, string leftWord, string right, string rightWord) =>
        Assert.Equal(
            left.IndexOf(leftWord, StringComparison.Ordinal),
            right.IndexOf(rightWord, StringComparison.Ordinal));

    private static void AssertRightEdgesAgree(string left, string leftWord, string right, string rightWord) =>
        Assert.Equal(
            left.IndexOf(leftWord, StringComparison.Ordinal) + leftWord.Length,
            right.IndexOf(rightWord, StringComparison.Ordinal) + rightWord.Length);

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
