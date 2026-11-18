using Npgsql;

namespace LexiSharp.Postgres;

/// <summary>
/// The gate and rank expressions one query is matched with, whichever side analysed it.
/// </summary>
/// <remarks>
/// Two implementations, one shape: <see cref="PostgresMatchExpression"/> for
/// <see cref="PostgresAnalysis.Database"/> (the server analyses the query) and
/// <see cref="PostgresLexiSharpMatch"/> for <see cref="PostgresAnalysis.LexiSharp"/> (this library
/// does). The engine reads the gate, the rank and the parameter binder through this interface so
/// the candidate query has one code path and the two modes cannot drift in how they bind a phrase.
/// </remarks>
internal interface IPostgresMatch
{
    /// <summary>The <c>tsquery</c> predicate a row must match.</summary>
    string Gate { get; }

    /// <summary>The expression <c>ts_rank_cd</c> is called with.</summary>
    string Rank { get; }

    /// <summary>
    /// Whether the gate is made of quoted phrases. A gate that is <c>false</c> matches the query's
    /// terms by the match mode, and the rare-term gate may stand in for it; a gate that is
    /// <c>true</c> is the query's contract and nothing may be substituted for it.
    /// </summary>
    bool HasPhrases { get; }

    /// <summary>Adds the query's parameters to a command, under the names the expressions use.</summary>
    void Bind(NpgsqlCommand command);
}
