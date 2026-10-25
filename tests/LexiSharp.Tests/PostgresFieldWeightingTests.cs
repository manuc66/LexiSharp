using LexiSharp.Core;
using LexiSharp.Postgres;
using Npgsql;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Field weighting on the <c>tsvector</c> path: the expression the three writing engines derive,
/// and the ranking a live <c>ts_rank_cd</c> produces from it.
/// </summary>
/// <remarks>
/// The expression half runs without a server, because it is pure string construction and a
/// regression there is invisible to a suite that skips. The live half self-skips without
/// <c>POSTGRES_TEST_CONNECTION</c>, like every other integration test.
/// </remarks>
public class PostgresFieldWeightingTests
{
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION");

    private static readonly Dictionary<string, PostgresFieldWeight> TitleAboveBody = new()
    {
        ["title"] = PostgresFieldWeight.A,
    };

    // ---- the expression, without a server -------------------------------------------------------

    /// <summary>
    /// With <see cref="PostgresIndexOptions.FieldWeights"/> unset, the expression is the one a table
    /// has always been written with. Asserted as the exact string rather than "contains
    /// to_tsvector", because the point of the default is that a table written before the option
    /// existed cannot start scoring differently.
    /// </summary>
    [Fact]
    public void WithoutFieldWeights_TheExpressionIsUnchanged()
    {
        var options = new PostgresIndexOptions { Table = "t" };

        Assert.Equal(
            "to_tsvector('simple', unaccent(@content))",
            PostgresSchema.TsvExpression(options, "@content"));

        Assert.Equal(
            "to_tsvector('simple', unaccent(@content || ' ' || "
            + "coalesce((select string_agg(value, ' ') from jsonb_each_text(@text_fields)), '')))",
            PostgresSchema.TsvExpression(options, "@content", "@text_fields"));

        Assert.Null(PostgresSchema.RankWeightsArray(options));
    }

    /// <summary>An empty map is the same as no map: nothing was asked for, so nothing changes.</summary>
    [Fact]
    public void AnEmptyWeightMap_LeavesTheExpressionAlone()
    {
        var options = new PostgresIndexOptions
        {
            Table = "t",
            FieldWeights = new Dictionary<string, PostgresFieldWeight>(),
        };

        Assert.Equal(
            "to_tsvector('simple', unaccent(@content || ' ' || "
            + "coalesce((select string_agg(value, ' ') from jsonb_each_text(@text_fields)), '')))",
            PostgresSchema.TsvExpression(options, "@content", "@text_fields"));

        Assert.Null(PostgresSchema.RankWeightsArray(options));

        // Accepted by the engine too: a caller who builds the map from configuration gets an empty
        // one when nothing is configured, and that is the unweighted default, not a bad option.
        const string unused = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none";

        using (new PostgresTextSearchEngine(unused, options, autoCreateSchema: false))
        {
        }
    }

    /// <summary>
    /// The weighted expression: content at the default class, the unlisted fields aggregated into
    /// one section at the default class, and every named field in a vector of its own at its class.
    /// </summary>
    [Fact]
    public void WithFieldWeights_EachSectionGetsItsOwnWeightedVector()
    {
        var options = new PostgresIndexOptions
        {
            Table = "t",
            FieldWeights = TitleAboveBody,
        };

        string expression = PostgresSchema.TsvExpression(options, "@content", "@text_fields");

        Assert.StartsWith("setweight(to_tsvector('simple', unaccent(@content)), 'D')", expression, StringComparison.Ordinal);

        // Unlisted entries stay searchable, at the default class, and are excluded from the
        // per-field sections rather than being counted twice.
        Assert.Contains(
            "setweight(to_tsvector('simple', unaccent(coalesce((select string_agg(value, ' ' order by key) "
            + "from jsonb_each_text(@text_fields) where key not in ('title')), ''))), 'D')",
            expression,
            StringComparison.Ordinal);

        Assert.Contains(
            "setweight(to_tsvector('simple', unaccent(coalesce(@text_fields ->> 'title', ''))), 'A')",
            expression,
            StringComparison.Ordinal);

        Assert.Equal("'{0.1, 0.2, 0.4, 1.0}'", PostgresSchema.RankWeightsArray(options));
    }

    /// <summary>
    /// The expression is a function of the configuration, not of the dictionary's enumeration
    /// order: the same fields in a different order must produce the same bytes, or the stored
    /// term positions depend on how the caller happened to build the map.
    /// </summary>
    [Fact]
    public void TheExpression_DoesNotDependOnTheDictionaryOrder()
    {
        var first = new PostgresIndexOptions
        {
            Table = "t",
            FieldWeights = new Dictionary<string, PostgresFieldWeight>
            {
                ["title"] = PostgresFieldWeight.A,
                ["summary"] = PostgresFieldWeight.B,
            },
        };

        var second = new PostgresIndexOptions
        {
            Table = "t",
            FieldWeights = new Dictionary<string, PostgresFieldWeight>
            {
                ["summary"] = PostgresFieldWeight.B,
                ["title"] = PostgresFieldWeight.A,
            },
        };

        Assert.Equal(
            PostgresSchema.TsvExpression(first, "@content", "@text_fields"),
            PostgresSchema.TsvExpression(second, "@content", "@text_fields"));
    }

    /// <summary>
    /// A field name is caller text, so the literal it becomes doubles its quotes. Without this a
    /// field called <c>it's</c> is a syntax error at write time, not an injection — but a syntax
    /// error only on the document that carries it.
    /// </summary>
    [Fact]
    public void AFieldNameWithAQuote_IsEscapedIntoTheLiteral()
    {
        var options = new PostgresIndexOptions
        {
            Table = "t",
            FieldWeights = new Dictionary<string, PostgresFieldWeight> { ["it's"] = PostgresFieldWeight.B },
        };

        string expression = PostgresSchema.TsvExpression(options, "@content", "@text_fields");

        Assert.Contains("key not in ('it''s')", expression, StringComparison.Ordinal);
        Assert.Contains("@text_fields ->> 'it''s'", expression, StringComparison.Ordinal);
    }

    /// <summary>
    /// The content reference without a text-fields one is weighted too, so an engine that carries no
    /// fields still writes a class — otherwise the query side's weights array would read a
    /// <c>D</c> the writer never wrote.
    /// </summary>
    [Fact]
    public void WithoutTextFields_TheContentIsStillWeighted()
    {
        var options = new PostgresIndexOptions { Table = "t", FieldWeights = TitleAboveBody };

        Assert.Equal(
            "setweight(to_tsvector('simple', unaccent(@content)), 'D')",
            PostgresSchema.TsvExpression(options, "@content"));
    }

    /// <summary>
    /// All three engines write the shared table, so all three must derive the same <c>tsv</c> from
    /// the same document. This is the expression half of that claim; the live test below is the
    /// stored half.
    /// </summary>
    [Fact]
    public void TheVectorAndSparseOptions_BuildTheSameExpressionAsTheLexicalEngine()
    {
        var lexical = new PostgresIndexOptions
        {
            Table = "t",
            FieldWeights = TitleAboveBody,
            DefaultFieldWeight = PostgresFieldWeight.B,
        };

        var vector = new PostgresVectorOptions
        {
            Table = "t",
            FieldWeights = TitleAboveBody,
            DefaultFieldWeight = PostgresFieldWeight.B,
        };

        var sparse = new PostgresSparseOptions
        {
            Table = "t",
            Vocabulary = new Dictionary<string, int> { ["term"] = 0 },
            FieldWeights = TitleAboveBody,
            DefaultFieldWeight = PostgresFieldWeight.B,
        };

        string expected = PostgresSchema.TsvExpression(lexical, "EXCLUDED.content", "EXCLUDED.text_fields");

        Assert.Equal(expected, PostgresSchema.TsvExpression(vector.AsIndexOptions(), "EXCLUDED.content", "EXCLUDED.text_fields"));
        Assert.Equal(expected, PostgresSchema.TsvExpression(sparse.AsIndexOptions(), "EXCLUDED.content", "EXCLUDED.text_fields"));

        // Including the default class, which is what makes an unlisted field's contribution agree.
        Assert.Contains("), 'B')", expected, StringComparison.Ordinal);
    }

    /// <summary>
    /// A field name that cannot become a literal is refused at construction, before a connection
    /// is opened — the same order the identifier guard runs in.
    /// </summary>
    [Fact]
    public void AWeightForAnEmptyFieldName_IsRejected()
    {
        const string unused = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none";

        var options = new PostgresIndexOptions
        {
            FieldWeights = new Dictionary<string, PostgresFieldWeight> { [""] = PostgresFieldWeight.A },
        };

        Assert.Throws<ArgumentException>(() => new PostgresTextSearchEngine(unused, options));
    }

    // ---- the ranking, against a server ----------------------------------------------------------

    private sealed class StubEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimension => 2;

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text, EmbeddingUse use, CancellationToken cancellationToken = default) =>
            Task.FromResult((ReadOnlyMemory<float>)new[] { 1f, 0f });
    }

    private static PostgresTextSearchEngine NewLexical(PostgresIndexOptions options) =>
        new(ConnectionString!, options with { Table = $"{options.Table}_{Guid.NewGuid().ToString("N")[..8]}" });

    /// <summary>
    /// A match in the title and a match in the body are the same score without weights, and a
    /// title match is ten times a body match with <c>title = A</c> against the default <c>D</c> —
    /// which is <c>ts_rank_cd</c>'s own 1.0 against 0.1, not a number this library chose.
    /// </summary>
    [SkippableFact]
    public void ATitleMatch_OutranksTheSameTermInTheBody_WhenTitleIsWeighted()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        const string term = "pineapple";

        var headers = new SearchDocument("header", "unrelated prose",
            TextFields: new Dictionary<string, string> { ["title"] = term });
        var body = new SearchDocument("body", term);

        using var neutral = NewLexical(new PostgresIndexOptions { Table = "lexisharp_weight_neutral" });
        try
        {
            neutral.Index([headers, body]);

            var results = neutral.Search(term, new SearchOptions(Limit: 10));

            Assert.Equal(2, results.Count);
            Assert.Equal(results[0].Score, results[1].Score, 6);
        }
        finally
        {
            neutral.DropSchema();
        }

        using var weighted = NewLexical(new PostgresIndexOptions
        {
            Table = "lexisharp_weight_title",
            FieldWeights = TitleAboveBody,
        });

        try
        {
            weighted.Index([headers, body]);

            var results = weighted.Search(term, new SearchOptions(Limit: 10));

            Assert.Equal(2, results.Count);
            Assert.Equal("header", results[0].DocumentId);
            Assert.Equal(1.0, results[0].Score, 6);
            Assert.Equal(0.1, results[1].Score, 6);
        }
        finally
        {
            weighted.DropSchema();
        }
    }

    /// <summary>
    /// A field the weights map does not name is still indexed, at the default class. Setting a
    /// weight for one field must not narrow the index to that field.
    /// </summary>
    [SkippableFact]
    public void AnUnlistedTextField_RemainsSearchable()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        using var engine = NewLexical(new PostgresIndexOptions
        {
            Table = "lexisharp_weight_unlisted",
            FieldWeights = TitleAboveBody,
        });

        try
        {
            engine.Add(new SearchDocument("a", "the body mentions nothing useful",
                TextFields: new Dictionary<string, string>
                {
                    ["title"] = "a weighted title",
                    ["summary"] = "papaya",
                }));

            var results = engine.Search("papaya", new SearchOptions(Limit: 10));

            Assert.Single(results);
            Assert.Equal("a", results[0].DocumentId);
        }
        finally
        {
            engine.DropSchema();
        }
    }

    /// <summary>
    /// The upsert path (<c>ON CONFLICT DO UPDATE</c>) writes the same expression as the insert
    /// path, so re-adding a document cannot leave the previous title's weighting behind.
    /// </summary>
    [SkippableFact]
    public void TheUpsertPath_RewritesTheWeightedVector()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        using var engine = NewLexical(new PostgresIndexOptions
        {
            Table = "lexisharp_weight_upsert",
            FieldWeights = TitleAboveBody,
        });

        try
        {
            engine.Add(new SearchDocument("a", "body",
                TextFields: new Dictionary<string, string> { ["title"] = "original headline" }));
            engine.Add(new SearchDocument("a", "body",
                TextFields: new Dictionary<string, string> { ["title"] = "replacement headline" }));

            Assert.Empty(engine.Search("original"));

            var results = engine.Search("replacement", new SearchOptions(Limit: 10));

            Assert.Single(results);
            Assert.Equal(1.0, results[0].Score, 6);
        }
        finally
        {
            engine.DropSchema();
        }
    }

    /// <summary>
    /// The stored <c>tsv</c> is the same whichever engine wrote the row, which is the property that
    /// makes a table shared by the lexical and vector engines score one document one way.
    /// </summary>
    [SkippableFact]
    public void TheLexicalAndVectorEngines_StoreTheSameTsv()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        var document = new SearchDocument("a", "the body text",
            TextFields: new Dictionary<string, string> { ["title"] = "a title", ["summary"] = "a summary" });

        string lexicalTable = $"lexisharp_weight_shared_lex_{Guid.NewGuid().ToString("N")[..8]}";
        string vectorTable = $"lexisharp_weight_shared_vec_{Guid.NewGuid().ToString("N")[..8]}";

        using var lexical = new PostgresTextSearchEngine(
            ConnectionString!,
            new PostgresIndexOptions
            {
                Table = lexicalTable,
                FieldWeights = TitleAboveBody,
                DefaultFieldWeight = PostgresFieldWeight.C,
            });

        using var vector = new PostgresVectorSearchEngine(
            ConnectionString!,
            new StubEmbeddingProvider(),
            new PostgresVectorOptions
            {
                Table = vectorTable,
                Dimension = 2,
                FieldWeights = TitleAboveBody,
                DefaultFieldWeight = PostgresFieldWeight.C,
            });

        try
        {
            lexical.Add(document);
            vector.Add(document);

            Assert.Equal(ReadTsv(lexicalTable), ReadTsv(vectorTable));
        }
        finally
        {
            lexical.DropSchema();
            vector.DropSchema();
        }

        string? ReadTsv(string table)
        {
            using var connection = new NpgsqlConnection(ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT tsv::text FROM public.{table} WHERE id = 'a'"; // NOSONAR:S2077 (test-owned name)
            return command.ExecuteScalar() as string;
        }
    }
}
