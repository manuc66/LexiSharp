using LexiSharp.Linguistics;

namespace LexiSharp.Core;

/// <summary>
/// The structured form of a raw query: free terms, quoted phrase constraints, expansion
/// atoms, and the combined literal term list a scorer starts from.
/// </summary>
/// <param name="FreeTerms">Terms outside double quotes, in reading order (literal terms only).</param>
/// <param name="Phrases">
/// Terms of each quoted segment, in query order. Every phrase must appear at consecutive
/// document positions; segments that tokenize to zero terms are dropped (a vacuous phrase
/// imposes no constraint). Operators are literal inside quotes.
/// </param>
/// <param name="AllTerms">
/// Free terms followed by the phrase terms — the literal part of the scoring input.
/// <see cref="Expansions"/> resolve later against the index vocabulary and are not included.
/// </param>
/// <param name="Expansions">
/// Free-text atoms carrying a <c>term*</c>/<c>term~N</c> operator, in reading order; empty
/// when the query has none.
/// </param>
public sealed record ParsedQuery(
    IReadOnlyList<string> FreeTerms,
    IReadOnlyList<IReadOnlyList<string>> Phrases,
    IReadOnlyList<string> AllTerms,
    IReadOnlyList<QueryExpansion> Expansions)
{
    /// <summary>Whether at least one positional phrase constraint applies.</summary>
    public bool HasPhrases => Phrases.Count > 0;

    /// <summary>Whether at least one prefix/fuzzy atom applies.</summary>
    public bool HasExpansions => Expansions.Count > 0;
}

/// <summary>
/// The raw, un-tokenized quote-level split of a query: text outside quotes and the literal
/// interior of each quoted segment. SQL backends forward these strings to their native phrase
/// operators, which tokenize with the database's own tokenizer.
/// </summary>
/// <param name="FreeText">Outside-quote segments, trimmed and joined by single spaces.</param>
/// <param name="Phrases">Interiors of the quoted segments, in query order.</param>
public sealed record RawQuerySegments(
    string FreeText,
    IReadOnlyList<string> Phrases);

/// <summary>
/// Splits a raw query on double quotes <b>before</b> tokenization — the tokenizer treats
/// <c>"</c> as an ordinary separator and would destroy the delimiters — and recognizes the
/// free-text expansion operators <c>term*</c> (prefix) and <c>term~</c>/<c>term~N</c> (fuzzy).
/// </summary>
/// <remarks>
/// An unterminated quote extends its phrase to the end of the query. A quote-only query
/// (<c>""</c>, <c>"   "</c>, <c>"!!!"</c>) contributes no terms at all, so a query made
/// solely of such segments parses to an empty <see cref="ParsedQuery.AllTerms"/> and matches
/// nothing. An expansion operator is only recognized when suffixed to a word-character run;
/// anything else (a leading <c>~</c>, a bare <c>*</c>) stays plain text, and an atom whose
/// base tokenizes to zero terms falls back to plain text too — byte-for-byte the terms of
/// <c>ITokenizer.Tokenize</c> with the operators treated as separators.
/// </remarks>
public static class QueryParser
{
    /// <summary>
    /// Tokenizes <paramref name="query"/> into free terms, phrase constraints and expansion
    /// atoms. A query without quotes or operators yields exactly
    /// <c>tokenizer.Tokenize(query)</c> as its free terms — byte-for-byte parity with plain
    /// tokenization.
    /// </summary>
    public static ParsedQuery Parse(string query, ITokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(tokenizer);

        if (!query.Contains('"') && !ContainsExpansionMarker(query))
        {
            var plainTerms = tokenizer.Tokenize(query);
            return new ParsedQuery(
                plainTerms,
                Array.Empty<IReadOnlyList<string>>(),
                plainTerms,
                Array.Empty<QueryExpansion>());
        }

        var freeTerms = new List<string>();
        var phrases = new List<IReadOnlyList<string>>();
        var expansions = new List<QueryExpansion>();

        int freeStart = 0;
        int phraseStart = 0;
        bool inPhrase = false;

        for (int i = 0; i < query.Length; i++)
        {
            if (query[i] != '"')
                continue;

            if (!inPhrase)
            {
                if (i > freeStart)
                    AppendFree(freeTerms, expansions, query[freeStart..i], tokenizer);

                inPhrase = true;
                phraseStart = i + 1;
            }
            else
            {
                AppendPhrase(phrases, tokenizer.Tokenize(query[phraseStart..i]));
                inPhrase = false;
                freeStart = i + 1;
            }
        }

        if (inPhrase)
            AppendPhrase(phrases, tokenizer.Tokenize(query[phraseStart..]));
        else if (freeStart < query.Length)
            AppendFree(freeTerms, expansions, query[freeStart..], tokenizer);

        var allTerms = new List<string>(freeTerms);

        foreach (var phrase in phrases)
        {
            for (int i = 0; i < phrase.Count; i++)
                allTerms.Add(phrase[i]);
        }

        return new ParsedQuery(freeTerms, phrases, allTerms, expansions);
    }

    /// <summary>
    /// Quote-level split without tokenizing: the free text (outside-quote segments, trimmed and
    /// joined by single spaces) and each quoted segment's literal interior. Interiors that are
    /// empty, whitespace-only, or that the default tokenizer empties are dropped — the same
    /// vacuous-phrase rule as <see cref="Parse"/>.
    /// </summary>
    public static RawQuerySegments SplitRaw(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!query.Contains('"'))
            return new RawQuerySegments(query, Array.Empty<string>());

        var freeParts = new List<string>();
        var phrases = new List<string>();

        int freeStart = 0;
        int phraseStart = 0;
        bool inPhrase = false;

        for (int i = 0; i < query.Length; i++)
        {
            if (query[i] != '"')
                continue;

            if (!inPhrase)
            {
                if (i > freeStart)
                {
                    var part = query[freeStart..i].Trim();
                    if (part.Length > 0)
                        freeParts.Add(part);
                }

                inPhrase = true;
                phraseStart = i + 1;
            }
            else
            {
                AppendRawPhrase(phrases, query[phraseStart..i]);
                inPhrase = false;
                freeStart = i + 1;
            }
        }

        if (inPhrase)
            AppendRawPhrase(phrases, query[phraseStart..]);
        else if (freeStart < query.Length)
        {
            var trailing = query[freeStart..].Trim();
            if (trailing.Length > 0)
                freeParts.Add(trailing);
        }

        return new RawQuerySegments(string.Join(' ', freeParts), phrases);
    }

    private static void Append(List<string> target, IReadOnlyList<string> terms)
    {
        for (int i = 0; i < terms.Count; i++)
            target.Add(terms[i]);
    }

    private static bool ContainsExpansionMarker(string text) =>
        text.IndexOf('*') >= 0 || text.IndexOf('~') >= 0;

    /// <summary>
    /// Appends one free-text (outside-quote) segment: plain terms plus any
    /// <c>term*</c>/<c>term~N</c> expansion atoms, in reading order. Text between atoms
    /// tokenizes normally; an atom whose base does not tokenize to exactly one term falls
    /// back to plain text (the operators are separators for the tokenizer).
    /// </summary>
    private static void AppendFree(
        List<string> terms,
        List<QueryExpansion> expansions,
        string segment,
        ITokenizer tokenizer)
    {
        if (segment.Length == 0)
            return;

        if (!ContainsExpansionMarker(segment))
        {
            Append(terms, tokenizer.Tokenize(segment));
            return;
        }

        int plainStart = 0;
        int i = 0;

        while (i < segment.Length)
        {
            if (!TryReadExpansionOperator(segment, i, out int runStart, out int atomEnd, out int maxEdits))
            {
                i++;
                continue;
            }

            if (runStart > plainStart)
                Append(terms, tokenizer.Tokenize(segment[plainStart..runStart]));

            var baseTerms = tokenizer.Tokenize(segment[runStart..i]);

            if (baseTerms.Count == 1)
            {
                expansions.Add(new QueryExpansion(
                    baseTerms[0],
                    segment[i] == '*' ? QueryExpansionKind.Prefix : QueryExpansionKind.Fuzzy,
                    maxEdits));
            }
            else
            {
                // Not a usable base (e.g. a dropped single character): keep the atom as plain text.
                Append(terms, tokenizer.Tokenize(segment[runStart..atomEnd]));
            }

            plainStart = atomEnd;
            i = atomEnd;
        }

        if (plainStart < segment.Length)
            Append(terms, tokenizer.Tokenize(segment[plainStart..]));
    }

    /// <summary>
    /// Reads the expansion operator at <paramref name="index"/> (<c>*</c>, or <c>~</c> with an
    /// optional edit count) when it is suffixed to a word-character run — reports the run
    /// start, the atom end (operator + digits) and the clamped fuzzy budget
    /// (<c>[0, 2]</c>, default 1).
    /// </summary>
    private static bool TryReadExpansionOperator(
        string segment,
        int index,
        out int runStart,
        out int atomEnd,
        out int maxEdits)
    {
        runStart = index;
        atomEnd = index + 1;
        maxEdits = 1;

        char marker = segment[index];

        if (marker is not ('*' or '~') || index == 0 || !char.IsLetterOrDigit(segment[index - 1]))
            return false;

        while (runStart > 0 && char.IsLetterOrDigit(segment[runStart - 1]))
            runStart--;

        if (marker == '~')
            atomEnd = ReadEditCount(segment, index + 1, out maxEdits);

        return true;
    }

    /// <summary>
    /// Consumes the ASCII digits after <c>~</c>; returns the end index (start when there are
    /// no digits) and the edit budget parsed from them, clamped to <c>[0, 2]</c>.
    /// </summary>
    private static int ReadEditCount(string segment, int start, out int maxEdits)
    {
        int edits = 0;
        int i = start;

        while (i < segment.Length && char.IsAsciiDigit(segment[i]))
        {
            edits = edits * 10 + (segment[i] - '0');

            if (edits > 2)
                edits = 2;

            i++;
        }

        bool hasDigits = i > start;
        maxEdits = hasDigits ? edits : 1;
        return hasDigits ? i : start;
    }

    private static void AppendPhrase(List<IReadOnlyList<string>> phrases, IReadOnlyList<string> terms)
    {
        if (terms.Count > 0)
            phrases.Add(terms);
    }

    private static void AppendRawPhrase(List<string> phrases, string interior)
    {
        if (string.IsNullOrWhiteSpace(interior))
            return;

        // Mirror Parse's rule with the default tokenizer as a proxy for "will any backend
        // produce a token from this": punctuation-only interiors impose no constraint.
        if (Tokenizer.Default.Tokenize(interior).Count == 0)
            return;

        phrases.Add(interior);
    }
}
