using Npgsql;

namespace LexiSharp.Postgres;

/// <summary>
/// PostgreSQL's query-time dictionaries, read from the server with <c>ts_lexize</c> — the one piece
/// of PostgreSQL analysis that stays useful when the index itself is analysed in the client.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <c>ts_lexize</c>.</b> It answers "what would this dictionary do to this one token?" and
/// returns the lexemes — no <c>tsquery</c>, no parser, no configuration. That is exactly the shape a
/// query expander needs: the token the client's analysis produced goes in, the dictionary's
/// normalisation (a stem, a thesaurus entry, an ispell correction) comes out, and the engine ORs it
/// beside the original. A thesaurus and an ispell dictionary both work through it, in either
/// analysis mode, without the server seeing the rest of the query.
/// </para>
/// <para>
/// <b>One statement per dictionary.</b> All of the query's terms travel as one <c>text[]</c>
/// parameter and come back row by row, so a six-word query against two dictionaries is two round
/// trips rather than twelve. Dictionary names are validated as <c>[A-Za-z0-9_]</c> by
/// <see cref="PostgresBm25Options"/> and quoted here; the value is passed as a parameter, so the
/// name reaches PostgreSQL as a <c>regdictionary</c> lookup and never as SQL.
/// </para>
/// <para>
/// A term the dictionary does not know comes back <c>NULL</c> and contributes nothing; a term it
/// knows but reduces to nothing (a stop word) comes back empty. Neither is an error, which is the
/// point: an expander that cannot help a term should leave the query as it was.
/// </para>
/// </remarks>
internal static class PostgresLexize
{
    /// <summary>
    /// Returns the lexemes the dictionaries produce for <paramref name="terms"/>, minus any the list
    /// already holds, deduplicated and in first-seen order.
    /// </summary>
    public static string[] Expand(
        NpgsqlConnection connection,
        IReadOnlyList<string> dictionaries,
        IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(dictionaries);
        ArgumentNullException.ThrowIfNull(terms);

        if (dictionaries.Count == 0 || terms.Count == 0)
            return [];

        var known = new HashSet<string>(terms, StringComparer.Ordinal);
        var expansions = new List<string>();

        string[] query = [.. terms];

        foreach (string dictionary in dictionaries)
        {
            using var command = connection.CreateCommand();

            // The dictionary is a validated identifier, passed as a parameter; the cast is the
            // server's own regdictionary lookup, which accepts a quoted identifier.
            command.CommandText = """
                SELECT ts_lexize(@dictionary::regdictionary, term)
                FROM unnest(@terms::text[]) AS term;
                """;

            command.Parameters.AddWithValue("dictionary", SafeIdentifier.QuoteIdentifier(dictionary));
            command.Parameters.AddWithValue("terms", query);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                if (reader.IsDBNull(0))
                    continue;

                foreach (string lexeme in reader.GetFieldValue<string[]>(0))
                {
                    if (lexeme.Length > 0 && known.Add(lexeme))
                        expansions.Add(lexeme);
                }
            }
        }

        return [.. expansions];
    }

    /// <summary>
    /// The query's terms followed by the expansions not already among them, deduplicated ordinally.
    /// </summary>
    public static string[] Merge(IReadOnlyList<string> terms, IReadOnlyList<string> expansions)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(expansions);

        if (expansions.Count == 0)
            return [.. terms];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<string>(terms.Count + expansions.Count);

        foreach (string term in terms)
        {
            if (seen.Add(term))
                merged.Add(term);
        }

        foreach (string expansion in expansions)
        {
            if (seen.Add(expansion))
                merged.Add(expansion);
        }

        return [.. merged];
    }
}
