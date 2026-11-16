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
/// <para>
/// <b>The <c>keep</c> set.</b> A search needs the frequency of the query's terms and nothing
/// else, but the length still needs every position. Passing the query's terms lets the reader skip
/// the <see cref="string"/> of every other lexeme: a candidate's <c>tsvector</c> holds a few
/// hundred lexemes and a query carries a handful, so without it the search allocates hundreds of
/// strings per candidate to throw nearly all of them away. The lookup is span-based
/// (<see cref="HashSet{T}.GetAlternateLookup{TAlternate}"/>), and the canonical string the set
/// already holds is what the frequency map is keyed by.
/// </para>
/// </remarks>
internal static class PostgresTsvText
{
    /// <summary>Parses a <c>tsvector</c> text value, keeping every lexeme.</summary>
    /// <param name="text">The value, as <c>tsv::text</c> produced it; empty for an empty vector.</param>
    /// <returns>Per-lexeme frequencies and the document length in indexed tokens.</returns>
    public static (Dictionary<string, int> TermFrequencies, int Length) Parse(string? text) =>
        Parse(text, keep: null);

    /// <summary>Parses a <c>tsvector</c> text value, keeping only the lexemes in <paramref name="keep"/>.</summary>
    /// <param name="text">The value, as <c>tsv::text</c> produced it; empty for an empty vector.</param>
    /// <param name="keep">
    /// The terms whose frequencies are wanted, or <c>null</c> for all of them. The length is exact
    /// either way: skipping a lexeme skips its string, not its positions.
    /// </param>
    /// <returns>Per-lexeme frequencies and the document length in indexed tokens.</returns>
    public static (Dictionary<string, int> TermFrequencies, int Length) Parse(string? text, HashSet<string>? keep)
    {
        var terms = new Dictionary<string, int>(StringComparer.Ordinal);
        int length = 0;

        if (string.IsNullOrEmpty(text))
            return (terms, length);

        HashSet<string>.AlternateLookup<ReadOnlySpan<char>> lookup = default;

        if (keep is not null)
            lookup = keep.GetAlternateLookup<ReadOnlySpan<char>>();

        int i = 0;

        while (TryReadLexeme(text, ref i, out int start, out int end, out bool escaped))
        {
            if (end <= start)
                continue;

            int count = ReadCount(text, ref i);
            length += count;

            Record(terms, text, start, end, escaped, count, keep, lookup);
        }

        return (terms, length);
    }

    /// <summary>
    /// Reads one lexeme at <paramref name="i"/>, skipping leading whitespace and advancing past the
    /// closing quote. Returns false at the end of the value.
    /// </summary>
    private static bool TryReadLexeme(
        string text, ref int i, out int start, out int end, out bool escaped)
    {
        start = 0;
        end = 0;
        escaped = false;

        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;

        if (i >= text.Length)
            return false;

        if (text[i] == '\'')
        {
            i++;
            start = i;

            while (i < text.Length)
            {
                if (text[i] == '\'')
                {
                    // A doubled quote is the escape; a single one closes the lexeme.
                    if (i + 1 < text.Length && text[i + 1] == '\'')
                    {
                        escaped = true;
                        i += 2;
                        continue;
                    }

                    break;
                }

                i++;
            }

            end = i;

            if (i < text.Length)
                i++;

            return true;
        }

        // Defensive: every released version quotes lexemes, but an unquoted one would otherwise be
        // swallowed into the next token's positions.
        start = i;

        while (i < text.Length && text[i] != ':' && !char.IsWhiteSpace(text[i]))
            i++;

        end = i;
        return true;
    }

    /// <summary>Adds one lexeme's occurrences to the frequency map, honoring the <c>keep</c> set.</summary>
    private static void Record(
        Dictionary<string, int> terms,
        string text,
        int start,
        int end,
        bool escaped,
        int count,
        HashSet<string>? keep,
        HashSet<string>.AlternateLookup<ReadOnlySpan<char>> lookup)
    {
        if (keep is null)
        {
            string lexeme = escaped ? Unescape(text, start, end) : text[start..end];
            terms[lexeme] = terms.GetValueOrDefault(lexeme) + count;
            return;
        }

        if (!escaped)
        {
            if (lookup.TryGetValue(text.AsSpan(start, end - start), out string? kept))
                terms[kept] = terms.GetValueOrDefault(kept) + count;

            return;
        }

        string unescaped = Unescape(text, start, end);

        if (keep.Contains(unescaped))
            terms[unescaped] = terms.GetValueOrDefault(unescaped) + count;
    }

    /// <summary>
    /// Reads the position list at <paramref name="i"/> and returns how many positions it holds,
    /// advancing <paramref name="i"/> past it. A lexeme with no list counts as one occurrence.
    /// </summary>
    private static int ReadCount(string text, ref int i)
    {
        if (i >= text.Length || text[i] != ':')
            return 1;

        i++;
        int count = 0;
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

            // A weight letter (or anything else) qualifies the position it follows; it neither
            // starts a new position nor ends the one it belongs to.
            i++;
        }

        if (digits)
            count++;

        // A malformed list still says the term is present.
        return Math.Max(count, 1);
    }

    /// <summary>An escaped lexeme as a string, with each doubled quote collapsed to one.</summary>
    private static string Unescape(string text, int start, int end)
    {
        var builder = new StringBuilder(end - start);

        for (int i = start; i < end; i++)
        {
            builder.Append(text[i]);

            if (text[i] == '\'' && i + 1 < end && text[i + 1] == '\'')
                i++;
        }

        return builder.ToString();
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
