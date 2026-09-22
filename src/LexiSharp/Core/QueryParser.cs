using LexiSharp.Linguistics;

namespace LexiSharp.Core;

/// <summary>
/// The structured form of a raw query: free terms, quoted phrase constraints, and the
/// combined term list a scorer sees.
/// </summary>
/// <param name="FreeTerms">Terms outside double quotes, in reading order.</param>
/// <param name="Phrases">
/// Terms of each quoted segment, in query order. Every phrase must appear at consecutive
/// document positions; segments that tokenize to zero terms are dropped (a vacuous phrase
/// imposes no constraint).
/// </param>
/// <param name="AllTerms">Free terms followed by the phrase terms — the scoring input.</param>
public sealed record ParsedQuery(
    IReadOnlyList<string> FreeTerms,
    IReadOnlyList<IReadOnlyList<string>> Phrases,
    IReadOnlyList<string> AllTerms)
{
    /// <summary>Whether at least one positional phrase constraint applies.</summary>
    public bool HasPhrases => Phrases.Count > 0;
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
/// <c>"</c> as an ordinary separator and would destroy the delimiters.
/// </summary>
/// <remarks>
/// An unterminated quote extends its phrase to the end of the query. A quote-only query
/// (<c>""</c>, <c>"   "</c>, <c>"!!!"</c>) contributes no terms at all, so a query made
/// solely of such segments parses to an empty <see cref="ParsedQuery.AllTerms"/> and matches
/// nothing.
/// </remarks>
public static class QueryParser
{
    /// <summary>
    /// Tokenizes <paramref name="query"/> into free terms and phrase constraints. A query
    /// without quotes yields exactly <c>tokenizer.Tokenize(query)</c> as its free terms —
    /// byte-for-byte parity with plain tokenization.
    /// </summary>
    public static ParsedQuery Parse(string query, ITokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(tokenizer);

        if (!query.Contains('"'))
        {
            var plainTerms = tokenizer.Tokenize(query);
            return new ParsedQuery(plainTerms, Array.Empty<IReadOnlyList<string>>(), plainTerms);
        }

        var freeTerms = new List<string>();
        var phrases = new List<IReadOnlyList<string>>();

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
                    Append(freeTerms, tokenizer.Tokenize(query[freeStart..i]));

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
            Append(freeTerms, tokenizer.Tokenize(query[freeStart..]));

        var allTerms = new List<string>(freeTerms);

        foreach (var phrase in phrases)
        {
            for (int i = 0; i < phrase.Count; i++)
                allTerms.Add(phrase[i]);
        }

        return new ParsedQuery(freeTerms, phrases, allTerms);
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
