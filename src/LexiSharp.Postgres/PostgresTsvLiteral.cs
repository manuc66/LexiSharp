using System.Text;

namespace LexiSharp.Postgres;

/// <summary>
/// Builds the text form of a <c>tsvector</c> from an already-analysed term stream, so PostgreSQL
/// stores a vector this library produced instead of one <c>to_tsvector</c> would have produced.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a literal and not a function.</b> <c>tsvector</c> is a type, not an analysis: the server
/// will happily parse a vector out of text. Handing it the terms the library's tokenizer emitted is
/// therefore the whole of client-side analysis on stock PostgreSQL — no extension, no server-side
/// function, no second copy of the analysis in SQL.
/// </para>
/// <para>
/// <b>Positions.</b> The term list is the document's term stream in reading
/// order — stop words already removed, stems already applied — and it becomes one position per
/// occurrence, 1-based, with lexemes sorted ordinally. The count of positions is the document's
/// indexed length, which is exactly what the in-memory index counts and what
/// <c>PostgresTsvText.Parse</c> reads back, so the two sides of a BM25 score agree.
/// </para>
/// <para>
/// There are no weight letters: a weight class is a <c>ts_rank_cd</c> concept, and this engine reads
/// term frequencies and scores them with BM25, which has no place for one. A document with no term
/// is the empty string — an empty vector, not <c>NULL</c> and not a parse error.
/// </para>
/// <para>
/// <b>Positions stop at the server's ceiling.</b> PostgreSQL stores a position in 16 bits, so the
/// largest a vector can hold is 16383 — the input function clamps anything above it, and two
/// identical positions collapse into one. A document longer than that is therefore recorded as
/// 16383 indexed tokens, and the occurrences past the ceiling are not counted in any term's
/// frequency. It is a real limit and it is stated rather than hidden: the reference analysis chops
/// a token at 255 units but puts no ceiling on their number, so a document of more than ~16k words
/// would differ from the in-memory index by its length term. No corpus this engine has been
/// measured on (FiQA, NFCorpus, SciFact) comes near it.
/// </para>
/// </remarks>
internal static class PostgresTsvLiteral
{
    /// <summary>The largest position a <c>tsvector</c> can store.</summary>
    internal const int MaximumPosition = 16383;

    /// <summary>
    /// Formats <paramref name="terms"/> as a <c>tsvector</c> literal, or the empty string when the
    /// list holds no lexeme.
    /// </summary>
    public static string Build(IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);

        if (terms.Count == 0)
            return string.Empty;

        var positions = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);

        for (int i = 0; i < terms.Count; i++)
        {
            string term = terms[i];

            // An empty lexeme carries nothing and cannot be written: the vector's grammar would
            // accept '':1, and every reader would count it as a token. The tokenizer never emits
            // one, so this is a guard rather than a case.
            if (string.IsNullOrEmpty(term))
                continue;

            if (!positions.TryGetValue(term, out List<int>? occurrences))
                positions[term] = occurrences = [];

            occurrences.Add(Math.Min(i + 1, MaximumPosition));
        }

        if (positions.Count == 0)
            return string.Empty;

        var literal = new StringBuilder(terms.Count * 8);

        foreach (var (term, occurrences) in positions)
        {
            if (literal.Length > 0)
                literal.Append(' ');

            // A quote inside a lexeme is doubled, which is the tsvector input grammar's only escape.
            literal.Append('\'')
                .Append(term.Replace("'", "''", StringComparison.Ordinal))
                .Append("':");

            for (int i = 0; i < occurrences.Count; i++)
            {
                if (i > 0)
                    literal.Append(',');

                literal.Append(occurrences[i]);
            }
        }

        return literal.ToString();
    }
}
