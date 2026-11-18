using System.Text;

using LexiSharp.Core;
using LexiSharp.Linguistics;
using Npgsql;

namespace LexiSharp.Postgres;

/// <summary>
/// The <c>tsquery</c> a <see cref="PostgresAnalysis.LexiSharp"/> search is matched with, built from
/// the same tokenizer that wrote the <c>tsv_bm25</c> column.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists beside <see cref="PostgresMatchExpression"/>.</b> That one hands the raw
/// query to PostgreSQL and lets the server's configuration analyse it. This mode took the analysis
/// away from the server, so the query has to be analysed here and the result handed back as a
/// <c>tsquery</c> literal — otherwise the index would hold one analysis' terms and the query
/// another's, which is exactly the gap the mode closes.
/// </para>
/// <para>
/// <b>The same contract.</b> Quoted segments are a hard gate (each must appear, AND-ed) while the
/// free terms only contribute to scoring, as in the in-memory engine. A query without quotes is
/// matched by <see cref="PostgresIndexOptions.MatchMode"/>.
/// </para>
/// <para>
/// <b>Compact positions.</b> A phrase is a chain of <c>&lt;-&gt;</c> operators over the terms the
/// analysis kept, and the column's positions were written the same way — one per kept term, so stop
/// words leave no hole. A phrase containing a stop word therefore requires the words around it to
/// be adjacent in the kept stream, where an engine indexing stop-word positions would require the
/// stop word itself. That difference is a property of "positions are the indexed tokens", and it is
/// what makes a phrase without a stop word behave exactly as expected.
/// </para>
/// <para>
/// <b>Parameters, not interpolated literals.</b> Each <c>tsquery</c> text reaches the statement as a
/// parameter cast with <c>::tsquery</c>: a lexeme is a document's data, and the cast is what makes
/// the server parse it as a query rather than trust the caller's string as SQL.
/// </para>
/// </remarks>
internal sealed class PostgresLexiSharpMatch : IPostgresMatch
{
    private readonly IReadOnlyList<(string Name, object Value)> _parameters;

    private PostgresLexiSharpMatch(
        string gate,
        string rank,
        string[] terms,
        bool hasPhrases,
        IReadOnlyList<(string Name, object Value)> parameters)
    {
        Gate = gate;
        Rank = rank;
        Terms = terms;
        HasPhrases = hasPhrases;
        _parameters = parameters;
    }

    /// <inheritdoc />
    public string Gate { get; }

    /// <inheritdoc />
    public string Rank { get; }

    /// <inheritdoc />
    public bool HasPhrases { get; }

    /// <summary>
    /// The query's terms, as the scorer sums over them: the free text's analysed terms followed by
    /// each analysed phrase's, in query order.
    /// </summary>
    public string[] Terms { get; }

    /// <summary>Builds the expression for one query.</summary>
    /// <param name="tokenizer">The tokenizer the column was written with — the same instance.</param>
    /// <param name="options">Naming and match-mode options.</param>
    /// <param name="column">The vector column to match: this package's <c>tsv_bm25</c> constant.</param>
    /// <param name="segments">The query split into free text and quoted phrases, before tokenization.</param>
    /// <param name="freeTerms">
    /// The free text's analysed terms, already carrying any dictionary expansion the caller asked
    /// for. Passed in rather than tokenized here because an expansion needs a round trip to the
    /// server for <c>ts_lexize</c>, and this class owns no connection.
    /// </param>
    public static PostgresLexiSharpMatch Build(
        ITokenizer tokenizer,
        PostgresIndexOptions options,
        string column,
        RawQuerySegments segments,
        IReadOnlyList<string> freeTerms)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(column);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(freeTerms);

        var phrases = new List<IReadOnlyList<string>>(segments.Phrases.Count);

        foreach (string phrase in segments.Phrases)
        {
            // A quoted segment the analysis empties — `"the of and"` — contributes nothing, which is
            // the vacuous-phrase rule the in-memory parser applies. Gating on an empty tsquery would
            // match no row at all, turning a phrase of stop words into "no results" where the
            // library's own contract says "no constraint".
            IReadOnlyList<string> terms = tokenizer.Tokenize(phrase);

            if (terms.Count > 0)
                phrases.Add(terms);
        }

        var scoringTerms = new List<string>(freeTerms.Count);
        scoringTerms.AddRange(freeTerms);

        foreach (IReadOnlyList<string> phrase in phrases)
            scoringTerms.AddRange(phrase);

        var parameters = new List<(string Name, object Value)>(phrases.Count + 1);

        string gate;
        string rank;

        if (phrases.Count == 0)
        {
            // No phrase: the free group is both the gate and the ranked query. Every kept term is
            // a real term here, so there is at least one — the caller returns early on an empty set.
            string free = Join(freeTerms, options.MatchMode == PostgresMatchMode.AnyTerm ? " | " : " & ");

            parameters.Add(("tsquery", free));

            gate = $"{column} @@ @tsquery::tsquery";
            rank = $"ts_rank_cd({column}, @tsquery::tsquery)";
        }
        else
        {
            var gates = new List<string>(phrases.Count);
            var ranks = new List<string>(phrases.Count + 1);

            if (freeTerms.Count > 0)
            {
                parameters.Add(("tsquery", Join(freeTerms, options.MatchMode == PostgresMatchMode.AnyTerm ? " | " : " & ")));
                ranks.Add("@tsquery::tsquery");
            }

            for (int i = 0; i < phrases.Count; i++)
            {
                parameters.Add(($"phrase{i}", Join(phrases[i], " <-> ")));
                gates.Add($"{column} @@ @phrase{i}::tsquery");

                // OR the free terms with each phrase so a phrase-only match still scores above zero,
                // the same shape the database-analysis expression uses.
                ranks.Add($"@phrase{i}::tsquery");
            }

            gate = string.Join(" AND ", gates);
            rank = $"ts_rank_cd({column}, {string.Join(" || ", ranks)})";
        }

        return new PostgresLexiSharpMatch(
            gate,
            rank,
            [.. scoringTerms],
            segments.Phrases.Count > 0,
            parameters);
    }

    private static string Join(IReadOnlyList<string> terms, string separator)
    {
        if (terms.Count == 0)
            return string.Empty;

        if (terms.Count == 1)
            return Lexeme(terms[0]);

        var builder = new StringBuilder(terms.Count * 12).Append('(');

        for (int i = 0; i < terms.Count; i++)
        {
            if (i > 0)
                builder.Append(separator);

            builder.Append(Lexeme(terms[i]));
        }

        return builder.Append(')').ToString();
    }

    /// <summary>A lexeme as the <c>tsquery</c> grammar spells it: quoted, with quotes doubled.</summary>
    private static string Lexeme(string term) =>
        "'" + term.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <inheritdoc />
    public void Bind(NpgsqlCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        foreach (var parameter in _parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
    }
}
