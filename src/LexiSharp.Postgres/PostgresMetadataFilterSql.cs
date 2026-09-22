using LexiSharp.Core;
using Npgsql;

namespace LexiSharp.Postgres;

/// <summary>
/// Translates <see cref="SearchOptions.Filters"/> into a parameterized SQL predicate over the
/// shared <c>fields jsonb</c> column, so the SQL backends honor the same filtering contract as
/// the in-memory engines instead of silently ignoring it.
/// </summary>
/// <remarks>
/// The fragment is pure SQL with parameter placeholders only — names and values are parameters,
/// never interpolated. Semantics mirror <see cref="MetadataFilter.Matches"/>:
/// <list type="bullet">
/// <item><description>Equal/NotEqual/Greater/LessThan compare ordinally via <c>COLLATE "C"</c>;
/// an absent field fails everything except NotEqual.</description></item>
/// <item><description>Greater/LessThan are numeric when both sides are valid <c>numeric</c> input,
/// otherwise ordinal — the <c>numeric</c> input syntax is PostgreSQL's approximation of the
/// in-memory <c>double.TryParse</c> check.</description></item>
/// <item><description>Contains is an ordinal substring test (<c>strpos</c>).</description></item>
/// </list>
/// </remarks>
internal static class PostgresMetadataFilterSql
{
    /// <summary>A SQL fragment (leading <c>" AND ..."</c>, or empty) plus the parameters it needs.</summary>
    internal readonly record struct FilterSql(string Fragment, IReadOnlyList<KeyValuePair<string, string>> Parameters)
    {
        /// <summary>No filters: an empty fragment and no parameters.</summary>
        public static readonly FilterSql None = new(string.Empty, Array.Empty<KeyValuePair<string, string>>());

        /// <summary>Binds the fragment's parameters to a command. Call once per command that embeds <see cref="Fragment"/>.</summary>
        public void Apply(NpgsqlCommand command)
        {
            for (int i = 0; i < Parameters.Count; i++)
                command.Parameters.AddWithValue(Parameters[i].Key, Parameters[i].Value);
        }
    }

    /// <summary>Builds the <c>" AND ..."</c> predicate for the given filters, or <see cref="FilterSql.None"/>.</summary>
    public static FilterSql Build(IReadOnlyList<MetadataFilter>? filters)
    {
        if (filters is null || filters.Count == 0)
            return FilterSql.None;

        var conditions = new List<string>(filters.Count);
        var parameters = new List<KeyValuePair<string, string>>(filters.Count * 2);

        for (int i = 0; i < filters.Count; i++)
        {
            var filter = filters[i];
            string keyParameter = $"mf{i}_k";
            string valueParameter = $"mf{i}_v";

            parameters.Add(new KeyValuePair<string, string>(keyParameter, filter.Field));
            parameters.Add(new KeyValuePair<string, string>(valueParameter, filter.Value));

            conditions.Add(Condition(filter.Operator, keyParameter, valueParameter));
        }

        return new FilterSql(" AND " + string.Join(" AND ", conditions), parameters);
    }

    private static string Condition(MetadataFilterOperator @operator, string keyParameter, string valueParameter) =>
        @operator switch
        {
            MetadataFilterOperator.Equal =>
                $"((fields ->> @{keyParameter}) COLLATE \"C\" = @{valueParameter} COLLATE \"C\")",
            // IS DISTINCT FROM treats an absent field (NULL) as "distinct from" any value, which is
            // exactly MetadataFilterOperator.NotEqual's absent-passes rule.
            MetadataFilterOperator.NotEqual =>
                $"((fields ->> @{keyParameter}) COLLATE \"C\" IS DISTINCT FROM @{valueParameter} COLLATE \"C\")",
            MetadataFilterOperator.Contains =>
                $"(strpos((fields ->> @{keyParameter}), @{valueParameter}) > 0)",
            MetadataFilterOperator.GreaterThan => Comparison(">", keyParameter, valueParameter),
            MetadataFilterOperator.LessThan => Comparison("<", keyParameter, valueParameter),
            _ => throw new InvalidOperationException($"Unknown filter operator {@operator}."),
        };

    private static string Comparison(string comparison, string keyParameter, string valueParameter) =>
        "CASE WHEN (fields ->> @" + keyParameter + ") IS NOT NULL" +
        $" AND pg_input_is_valid((fields ->> @{keyParameter}), 'numeric')" +
        $" AND pg_input_is_valid(@{valueParameter}, 'numeric')" +
        $" THEN (fields ->> @{keyParameter})::numeric {comparison} (@{valueParameter})::numeric" +
        $" ELSE (fields ->> @{keyParameter}) COLLATE \"C\" {comparison} @{valueParameter} COLLATE \"C\" END";
}
