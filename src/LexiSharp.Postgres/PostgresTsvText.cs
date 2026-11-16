using System.Text;

namespace LexiSharp.Postgres;

/// <summary>
/// Reads the text form of a <c>tsvector</c> into per-lexeme frequencies and a document length.
/// </summary>
/// <remarks>
/// <para>
/// This exists because PostgreSQL exposes no set-returning function for "the term frequencies of
/// one vector": <c>tsvector_to_array</c> drops positions and therefore duplicates (it returns
/// <c>{cat,fat,rat}</c> for <c>'fat:2,4 cat:3 rat:5A'</c>), and <c>ts_stat</c> only answers for a
/// whole table. The text form keeps the positions, and counting them is the frequency.
/// </para>
/// <para>
/// The grammar is the output of <c>tsvector_out</c>: lexemes are single-quoted, an embedded quote
/// is doubled, positions follow a colon as a comma-separated list, and a position may carry a
/// weight letter (<c>A</c>-<c>D</c>). A lexeme with no position list (a vector written by
/// <c>strip</c>) counts as one occurrence: the vector says the term is present, and no more.
/// </para>
/// <para>
/// The length is the number of positions, which is the number of indexed tokens — the quantity
/// <c>ts_stat</c>'s <c>nentry</c> sums to over the table, so the per-document length and the corpus
/// average are one measurement rather than two definitions.
/// </para>
/// </remarks>
internal static class PostgresTsvText
{
    /// <summary>Parses a <c>tsvector</c> text value.</summary>
    /// <param name="text">The value, as <c>tsv::text</c> produced it; empty for an empty vector.</param>
    /// <returns>Per-lexeme frequencies and the document length in indexed tokens.</returns>
    public static (Dictionary<string, int> TermFrequencies, int Length) Parse(string? text)
    {
        var terms = new Dictionary<string, int>(StringComparer.Ordinal);
        int length = 0;

        if (string.IsNullOrEmpty(text))
            return (terms, length);

        int i = 0;

        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;

            if (i >= text.Length)
                break;

            string lexeme;

            if (text[i] == '\'')
            {
                i++;
                var builder = new StringBuilder();

                while (i < text.Length)
                {
                    if (text[i] == '\'')
                    {
                        // A doubled quote is the escape; a single one closes the lexeme.
                        if (i + 1 < text.Length && text[i + 1] == '\'')
                        {
                            builder.Append('\'');
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    builder.Append(text[i]);
                    i++;
                }

                lexeme = builder.ToString();
            }
            else
            {
                // Defensive: every released version quotes lexemes, but an unquoted one would
                // otherwise be swallowed into the next token's positions.
                int start = i;

                while (i < text.Length && text[i] != ':' && !char.IsWhiteSpace(text[i]))
                    i++;

                lexeme = text[start..i];
            }

            if (lexeme.Length == 0)
                continue;

            int count = 1;

            if (i < text.Length && text[i] == ':')
            {
                i++;
                count = 0;
                bool digits = false;

                while (i < text.Length && !char.IsWhiteSpace(text[i]))
                {
                    char c = text[i];

                    if (c is >= '0' and <= '9')
                    {
                        digits = true;
                        i++;
                        continue;
                    }

                    if (c == ',')
                    {
                        if (digits)
                            count++;

                        digits = false;
                        i++;
                        continue;
                    }

                    // A weight letter (or anything else) qualifies the position it follows; it
                    // neither starts a new position nor ends the one it belongs to.
                    i++;
                }

                if (digits)
                    count++;

                // A malformed list still says the term is present.
                count = Math.Max(count, 1);
            }

            terms[lexeme] = terms.GetValueOrDefault(lexeme) + count;
            length += count;
        }

        return (terms, length);
    }

    /// <summary>
    /// Formats a term→frequency map as a <c>tsvector</c> literal, for tests that need a row without
    /// a server. Positions are sequential and carry no weights, which is all the BM25 mode reads.
    /// </summary>
    internal static string Format(IReadOnlyDictionary<string, int> termFrequencies)
    {
        ArgumentNullException.ThrowIfNull(termFrequencies);

        var builder = new StringBuilder();
        int position = 0;

        foreach (var entry in termFrequencies.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (builder.Length > 0)
                builder.Append(' ');

            builder.Append('\'').Append(entry.Key.Replace("'", "''", StringComparison.Ordinal)).Append('\'');

            if (entry.Value > 0)
            {
                builder.Append(':');

                for (int i = 0; i < entry.Value; i++)
                {
                    if (i > 0)
                        builder.Append(',');

                    builder.Append(++position);
                }
            }
        }

        return builder.ToString();
    }
}
