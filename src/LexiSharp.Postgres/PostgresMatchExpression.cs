using LexiSharp.Core;
using Npgsql;

namespace LexiSharp.Postgres;

/// <summary>
/// The <c>tsquery</c> a search is matched and ranked with, built once for both stock-PostgreSQL
/// text engines.
/// </summary>
/// <remarks>
/// <para>
/// Mixed queries honour the same contract as the in-memory engine: quoted segments are a hard
/// corpus gate (each phrase must appear, AND-ed) while the free terms only contribute to scoring.
/// Gating on the whole query would wrongly require the free terms too. A query without quotes is
/// matched by <see cref="PostgresIndexOptions.MatchMode"/>.
/// </para>
/// <para>
/// It lives here rather than in one engine because two of them issue it now —
/// <see cref="PostgresTextSearchEngine"/> scores with <c>ts_rank_cd</c>, and
/// <see cref="PostgresBm25SearchEngine"/> uses the same expression to choose the candidate pool it
/// then re-ranks. One copy is what keeps a query that means one thing at the gate from meaning
/// another at the rank.
/// </para>
/// </remarks>
internal sealed class PostgresMatchExpression
{
    private readonly IReadOnlyList<KeyValuePair<string, string>> _parameters;

    private PostgresMatchExpression(
        string gate, string rank, IReadOnlyList<KeyValuePair<string, string>> parameters)
    {
        Gate = gate;
        Rank = rank;
        _parameters = parameters;
    }

    /// <summary>The <c>tsquery</c> a row must match, as an SQL predicate on <c>tsv</c>.</summary>
    public string Gate { get; }

    /// <summary>The expression <c>ts_rank_cd</c> is called with, weights included.</summary>
    public string Rank { get; }

    /// <summary>Builds the expression for one query.</summary>
    /// <param name="options">Naming, analysis and match-mode options.</param>
    /// <param name="query">The raw query text.</param>
    public static PostgresMatchExpression Build(PostgresIndexOptions options, string query)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(query);

        var segments = QueryParser.SplitRaw(query);
        var parameters = new List<KeyValuePair<string, string>>(segments.Phrases.Count + 2);

        string gate;
        string rank;

        if (segments.Phrases.Count == 0)
        {
            string match = PostgresTsQueryText.For(options.MatchMode, options.TextSearchConfig, "@query");

            gate = $"tsv @@ {match}";
            rank = match;
            parameters.Add(new("query", query));
        }
        else
        {
            var gates = new List<string>(segments.Phrases.Count);
            var ranks = new List<string>(segments.Phrases.Count + 1);

            if (!string.IsNullOrWhiteSpace(segments.FreeText))
            {
                ranks.Add(PostgresTsQueryText.For(options.MatchMode, options.TextSearchConfig, "@freeText"));
                parameters.Add(new("freeText", segments.FreeText));
            }

            for (int i = 0; i < segments.Phrases.Count; i++)
            {
                gates.Add($"tsv @@ phraseto_tsquery('{options.TextSearchConfig}', unaccent(@phrase{i}))");
                ranks.Add($"phraseto_tsquery('{options.TextSearchConfig}', unaccent(@phrase{i}))");
                parameters.Add(new($"phrase{i}", segments.Phrases[i]));
            }

            gate = string.Join(" AND ", gates);

            // OR the free terms with each phrase so a phrase-only match still scores above zero
            // (the ts_rank_cd of the full AND query is 0 when a free term is missing).
            rank = string.Join(" || ", ranks);
        }

        // The weights array is the class-to-value mapping the stored letters are read through, so
        // the query side of a weighted index is stated here rather than left to ts_rank_cd's
        // default. Without FieldWeights the call is the one the engine has always issued.
        string rankSql = PostgresSchema.RankWeightsArray(options) is { } weights
            ? $"ts_rank_cd({weights}, tsv, {rank})"
            : $"ts_rank_cd(tsv, {rank})";

        return new PostgresMatchExpression(gate, rankSql, parameters);
    }

    /// <summary>Adds the query's parameters to a command, under the names the expressions use.</summary>
    public void Bind(NpgsqlCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        foreach (var parameter in _parameters)
            command.Parameters.AddWithValue(parameter.Key, parameter.Value);
    }
}
