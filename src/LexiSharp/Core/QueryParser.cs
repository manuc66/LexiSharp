using LexiSharp.Linguistics;

namespace LexiSharp.Core;

/// <summary>
/// The structured form of a raw query: free terms, quoted phrase constraints, expansion
/// atoms, and the combined literal term list a scorer starts from.
/// </summary>
/// <param name="FreeTerms">Terms outside double quotes, in reading order (literal terms only).</param>
/// <param name="Phrases">
/// Terms of each unqualified quoted segment, in query order. Every phrase must appear at
/// consecutive document positions; segments that tokenize to zero terms are dropped (a vacuous
/// phrase imposes no constraint). Operators are literal inside quotes unless one qualifies the
/// whole quoted segment (see <see cref="RequiredPhrases"/> and <see cref="ProhibitedPhrases"/>).
/// </param>
/// <param name="AllTerms">
/// The literal part of the <i>unscoped</i> scoring input: free terms, required (<c>+</c>) terms,
/// and the terms of unqualified and required phrases, in reading order. Prohibited (<c>-</c>)
/// terms are gates and are deliberately absent, and so are field-scoped atoms — those have a field
/// attached and live in <see cref="ScopedTerms"/>. <see cref="Expansions"/> resolve later against
/// the index vocabulary and are not included.
/// </param>
/// <param name="Expansions">
/// Free-text atoms carrying a <c>term*</c>/<c>term~N</c> operator, in reading order; empty when
/// the query has none. Each atom records its own <see cref="QueryExpansion.Operator"/>, so a
/// required or prohibited atom is distinguishable from an unqualified one.
/// </param>
public sealed record ParsedQuery(
    IReadOnlyList<string> FreeTerms,
    IReadOnlyList<IReadOnlyList<string>> Phrases,
    IReadOnlyList<string> AllTerms,
    IReadOnlyList<QueryExpansion> Expansions)
{
    /// <summary>
    /// Literal terms written with a leading <c>+</c>, in reading order. Each must occur in a
    /// document for it to match, and each also scores as the same term written without the
    /// operator.
    /// </summary>
    public IReadOnlyList<string> RequiredTerms { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Literal terms written with a leading <c>-</c>, in reading order. A document containing any
    /// one of them is excluded whatever it scores, and they contribute nothing to the score.
    /// </summary>
    public IReadOnlyList<string> ProhibitedTerms { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Terms of each quoted segment written with a leading <c>+</c>. Every such phrase must appear
    /// at consecutive document positions, and its terms also score.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> RequiredPhrases { get; init; } =
        Array.Empty<IReadOnlyList<string>>();

    /// <summary>
    /// Terms of each quoted segment written with a leading <c>-</c>. A document where any such
    /// phrase appears at consecutive positions is excluded, and its terms do not score.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> ProhibitedPhrases { get; init; } =
        Array.Empty<IReadOnlyList<string>>();

    /// <summary>
    /// The literal field-scoped terms that score: free <c>title:term</c> atoms, required ones, and
    /// the terms of unqualified and required scoped phrases, in reading order. Prohibited scoped
    /// atoms are absent. Each term scores from its field's statistics and contributes zero when
    /// the field does not carry it.
    /// </summary>
    public IReadOnlyList<FieldScopedTerm> ScopedTerms { get; init; } = Array.Empty<FieldScopedTerm>();

    /// <summary>
    /// Field-scoped terms written with a leading <c>+</c>. Each must occur in its field for the
    /// document to match, and each also scores (it is also in <see cref="ScopedTerms"/>).
    /// </summary>
    public IReadOnlyList<FieldScopedTerm> RequiredScopedTerms { get; init; } =
        Array.Empty<FieldScopedTerm>();

    /// <summary>
    /// Field-scoped terms written with a leading <c>-</c>. A document carrying one of them in its
    /// field is excluded whatever it scores, and they contribute nothing to the score.
    /// </summary>
    public IReadOnlyList<FieldScopedTerm> ProhibitedScopedTerms { get; init; } =
        Array.Empty<FieldScopedTerm>();

    /// <summary>
    /// Quoted segments written as <c>title:"a phrase"</c>. Each must appear at consecutive
    /// positions <i>inside its field</i>, and its terms score as
    /// <see cref="FieldScopedTerm"/>s.
    /// </summary>
    public IReadOnlyList<FieldScopedPhrase> ScopedPhrases { get; init; } =
        Array.Empty<FieldScopedPhrase>();

    /// <summary>
    /// Quoted segments written as <c>+title:"a phrase"</c>. Each must appear at consecutive
    /// positions inside its field, and its terms score.
    /// </summary>
    public IReadOnlyList<FieldScopedPhrase> RequiredScopedPhrases { get; init; } =
        Array.Empty<FieldScopedPhrase>();

    /// <summary>
    /// Quoted segments written as <c>-title:"a phrase"</c>. A document where any of them appears
    /// inside its field is excluded, and its terms do not score.
    /// </summary>
    public IReadOnlyList<FieldScopedPhrase> ProhibitedScopedPhrases { get; init; } =
        Array.Empty<FieldScopedPhrase>();

    /// <summary>Whether at least one positional phrase constraint applies, scoped or not.</summary>
    public bool HasPhrases =>
        Phrases.Count > 0 || RequiredPhrases.Count > 0 || ProhibitedPhrases.Count > 0
        || ScopedPhrases.Count > 0 || RequiredScopedPhrases.Count > 0
        || ProhibitedScopedPhrases.Count > 0;

    /// <summary>Whether at least one prefix/fuzzy atom applies.</summary>
    public bool HasExpansions => Expansions.Count > 0;

    /// <summary>Whether the query writes at least one field scope.</summary>
    public bool HasFieldScopes
    {
        get
        {
            if (ScopedTerms.Count > 0 || RequiredScopedTerms.Count > 0 || ProhibitedScopedTerms.Count > 0
                || ScopedPhrases.Count > 0 || RequiredScopedPhrases.Count > 0
                || ProhibitedScopedPhrases.Count > 0)
            {
                return true;
            }

            // A scoped prefix/fuzzy atom carries its field on the atom itself: its terms resolve
            // later, against the vocabulary, so no literal scoped term exists yet.
            for (int i = 0; i < Expansions.Count; i++)
            {
                if (Expansions[i].Field is not null)
                    return true;
            }

            return false;
        }
    }

    /// <summary>Whether the query writes at least one <c>+</c> operator.</summary>
    public bool HasRequired =>
        RequiredTerms.Count > 0 || RequiredPhrases.Count > 0
        || RequiredScopedTerms.Count > 0 || RequiredScopedPhrases.Count > 0
        || HasExpansionOperator(QueryTermOperator.Required);

    /// <summary>Whether the query writes at least one <c>-</c> operator.</summary>
    public bool HasProhibited =>
        ProhibitedTerms.Count > 0 || ProhibitedPhrases.Count > 0
        || ProhibitedScopedTerms.Count > 0 || ProhibitedScopedPhrases.Count > 0
        || HasExpansionOperator(QueryTermOperator.Prohibited);

    /// <summary>Whether the query uses <c>+</c> or <c>-</c> to qualify a term, phrase or atom.</summary>
    public bool HasQualifiers => HasRequired || HasProhibited;

    private bool HasExpansionOperator(QueryTermOperator op)
    {
        for (int i = 0; i < Expansions.Count; i++)
        {
            if (Expansions[i].Operator == op)
                return true;
        }

        return false;
    }
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
/// free-text expansion operators <c>term*</c> (prefix) and <c>term~</c>/<c>term~N</c> (fuzzy)
/// and the leading required/prohibited operators <c>+</c> and <c>-</c>.
/// </summary>
/// <remarks>
/// An unterminated quote extends its phrase to the end of the query. A quote-only query
/// (<c>""</c>, <c>"   "</c>, <c>"!!!"</c>) contributes no terms at all, so a query made
/// solely of such segments parses to an empty <see cref="ParsedQuery.AllTerms"/> and matches
/// nothing. An expansion operator is only recognized when suffixed to a word-character run;
/// anything else (a leading <c>~</c>, a bare <c>*</c>) stays plain text, and an atom whose
/// base tokenizes to zero terms falls back to plain text too — byte-for-byte the terms of
/// <c>ITokenizer.Tokenize</c> with the operators treated as separators.
/// <para>
/// <b>Required and prohibited operators.</b> A <c>+</c> or <c>-</c> is an operator only as the
/// first character of a whitespace-delimited token and only when something follows it: the token
/// for a term (<c>+term</c>, <c>-term*</c>) or the opening quote for a phrase (<c>+"a phrase"</c>,
/// <c>-"a phrase"</c>). A <c>-</c> anywhere else is literal, so <c>e-mail</c>, <c>well-being</c>
/// and <c>foo-bar</c> tokenize exactly as they do without the query language, and a bare <c>+</c>
/// or <c>-</c> tokenizes to nothing. When a token starts with several operator characters, the
/// first one decides: <c>++a</c> and <c>+-a</c> mean <c>+a</c>, <c>--a</c> and <c>-+a</c> mean
/// <c>-a</c>; the remaining leading characters are separators to the tokenizer. And a <c>+</c>/<c>-</c>
/// separated from its term or quote by whitespace is not an operator at all. A qualifier whose
/// segment produces no terms (a single character the tokenizer drops, an empty quoted segment) is
/// dropped with that segment, so a query made only of such tokens has nothing to match.
/// </para>
/// <para>
/// <b>Semantics.</b> Required and prohibited atoms are gates checked before scoring: a required
/// atom must be present (every literal term; at least one expanded term for a prefix/fuzzy atom;
/// the whole phrase for a quoted segment) and a prohibited one must be absent. A required term
/// also scores exactly as the same term written without <c>+</c>; a prohibited term never scores.
/// A query of only prohibited atoms is the complement of its gate, scored at <c>1</c> for every
/// surviving document — it carries no relevance signal, so the configured tie-break ranks it.
/// A query of only required atoms and no free term scores on those required terms.
/// </para>
/// <para>
/// <b>Field scopes.</b> When the caller supplies a field vocabulary (<c>fields</c>, the engine's
/// index fields or <see cref="SearchOptions.KnownFields"/>), a token whose first colon separates a
/// <i>known</i> name from a non-empty remainder scopes that atom to the field:
/// <c>title:term</c>, <c>title:"a phrase"</c>, <c>+title:term</c>, <c>-title:term</c>, and the
/// prefix/fuzzy forms <c>title:term*</c>/<c>title:term~N</c>. A name that is not in the vocabulary
/// is not a scope, so <c>http://x</c>, <c>12:30</c> and <c>key:value</c> tokenize exactly as they
/// did before the feature. The scope must begin the token (after an optional leading
/// <c>+</c>/<c>-</c>) and must be followed immediately by the atom: <c>title: term</c>,
/// <c>title:</c> alone, and <c>title:-term</c> are ordinary text, and a scope inside quotes is
/// literal. Field names are matched by the vocabulary's own comparer.
/// </para>
/// </remarks>
public static class QueryParser
{
    /// <summary>
    /// Tokenizes <paramref name="query"/> into free terms, phrase constraints and expansion
    /// atoms. A query without quotes, operators or a field scope yields exactly
    /// <c>tokenizer.Tokenize(query)</c> as its free terms — byte-for-byte parity with plain
    /// tokenization.
    /// </summary>
    /// <param name="query">The raw query.</param>
    /// <param name="tokenizer">The tokenizer the index was built with.</param>
    /// <param name="fields">
    /// The names a <c>name:</c> prefix may name. <c>null</c> means the query has no field grammar,
    /// so every colon is ordinary text — the default, which is what keeps this parser from
    /// guessing that <c>key:value</c> is a scope.
    /// </param>
    public static ParsedQuery Parse(
        string query,
        ITokenizer tokenizer,
        IReadOnlySet<string>? fields = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Parse(query.AsSpan(), tokenizer, fields);
    }

    /// <summary>
    /// Whether <paramref name="query"/> is plain: no double quote, no prefix or fuzzy operator, no
    /// leading required/prohibited operator and no field scope, so the query language has nothing
    /// to add to it. A plain query tokenizes to exactly <c>tokenizer.Tokenize(query)</c>, which is
    /// what lets a caller with no use for the phrases and expansions of a
    /// <see cref="ParsedQuery"/> skip building one.
    /// </summary>
    public static bool IsPlain(ReadOnlySpan<char> query, IReadOnlySet<string>? fields = null) =>
        query.IndexOf('"') < 0
        && !ContainsExpansionMarker(query)
        && !ContainsOperatorToken(query)
        && !ContainsFieldScope(query, fields);

    /// <summary>
    /// Tokenizes <paramref name="query"/> into free terms, phrase constraints and expansion
    /// atoms, without materializing the query as a string.
    /// </summary>
    public static ParsedQuery Parse(
        ReadOnlySpan<char> query,
        ITokenizer tokenizer,
        IReadOnlySet<string>? fields = null)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);

        if (IsPlain(query, fields))
        {
            var plainTerms = tokenizer.Tokenize(query);
            return new ParsedQuery(
                plainTerms,
                Array.Empty<IReadOnlyList<string>>(),
                plainTerms,
                Array.Empty<QueryExpansion>());
        }

        var freeTerms = new List<string>();
        var requiredTerms = new List<string>();
        var prohibitedTerms = new List<string>();
        var phrases = new List<IReadOnlyList<string>>();
        var requiredPhrases = new List<IReadOnlyList<string>>();
        var prohibitedPhrases = new List<IReadOnlyList<string>>();
        var expansions = new List<QueryExpansion>();
        var scope = new ScopeState();

        int freeStart = 0;
        int phraseStart = 0;
        bool inPhrase = false;
        QueryTermOperator phraseOperator = QueryTermOperator.None;
        string? phraseField = null;

        for (int i = 0; i < query.Length; i++)
        {
            if (query[i] != '"')
                continue;

            if (!inPhrase)
            {
                var segment = query[freeStart..i];
                var operatorBeforeQuote = TakeTrailingScope(ref segment, fields, out var fieldBeforeQuote);

                if (segment.Length > 0)
                {
                    AppendFreeSegment(
                        freeTerms, requiredTerms, prohibitedTerms, expansions, scope, segment, tokenizer, fields);
                }

                inPhrase = true;
                phraseStart = i + 1;
                phraseOperator = operatorBeforeQuote;
                phraseField = fieldBeforeQuote;
            }
            else
            {
                AppendPhrase(phraseOperator, phraseField, phrases, requiredPhrases, prohibitedPhrases, scope,
                    tokenizer.Tokenize(query[phraseStart..i]));
                inPhrase = false;
                phraseOperator = QueryTermOperator.None;
                phraseField = null;
                freeStart = i + 1;
            }
        }

        if (inPhrase)
        {
            AppendPhrase(phraseOperator, phraseField, phrases, requiredPhrases, prohibitedPhrases, scope,
                tokenizer.Tokenize(query[phraseStart..]));
        }
        else if (freeStart < query.Length)
        {
            AppendFreeSegment(
                freeTerms, requiredTerms, prohibitedTerms, expansions, scope,
                query[freeStart..], tokenizer, fields);
        }

        var allTerms = new List<string>(freeTerms.Count + requiredTerms.Count);

        allTerms.AddRange(freeTerms);
        allTerms.AddRange(requiredTerms);

        foreach (var phrase in phrases)
            Append(allTerms, phrase);

        foreach (var phrase in requiredPhrases)
            Append(allTerms, phrase);

        return new ParsedQuery(freeTerms, phrases, allTerms, expansions)
        {
            RequiredTerms = requiredTerms,
            ProhibitedTerms = prohibitedTerms,
            RequiredPhrases = requiredPhrases,
            ProhibitedPhrases = prohibitedPhrases,
            ScopedTerms = scope.Scoring,
            RequiredScopedTerms = scope.Required,
            ProhibitedScopedTerms = scope.Prohibited,
            ScopedPhrases = scope.Phrases,
            RequiredScopedPhrases = scope.RequiredPhrases,
            ProhibitedScopedPhrases = scope.ProhibitedPhrases,
        };
    }

    /// <summary>
    /// Quote-level split without tokenizing: the free text (outside-quote segments, trimmed and
    /// joined by single spaces) and each quoted segment's literal interior. Interiors that are
    /// empty, whitespace-only, or that the default tokenizer empties are dropped — the same
    /// vacuous-phrase rule as <see cref="Parse(string, ITokenizer, IReadOnlySet{string})"/>.
    /// </summary>
    /// <remarks>
    /// This is a raw split and stays one: a leading <c>+</c>/<c>-</c> operator remains part of the
    /// free text here, because only the tokenizing parse can decide whether it qualifies a term.
    /// </remarks>
    public static RawQuerySegments SplitRaw(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return SplitRaw(query.AsSpan());
    }

    /// <summary>
    /// Quote-level split without tokenizing, without materializing the query as a string.
    /// </summary>
    public static RawQuerySegments SplitRaw(ReadOnlySpan<char> query)
    {
        if (query.IndexOf('"') < 0)
            return new RawQuerySegments(query.ToString(), Array.Empty<string>());

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
                        freeParts.Add(part.ToString());
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
                freeParts.Add(trailing.ToString());
        }

        return new RawQuerySegments(string.Join(' ', freeParts), phrases);
    }

    private static void Append(List<string> target, IReadOnlyList<string> terms)
    {
        for (int i = 0; i < terms.Count; i++)
            target.Add(terms[i]);
    }

    private static bool ContainsExpansionMarker(ReadOnlySpan<char> text) =>
        text.IndexOf('*') >= 0 || text.IndexOf('~') >= 0;

    /// <summary>
    /// Whether any whitespace-delimited token starts with a <c>+</c>/<c>-</c> that has something
    /// after it — the cheap test that keeps a query with no operator on the exact plain path.
    /// </summary>
    private static bool ContainsOperatorToken(ReadOnlySpan<char> text)
    {
        int i = 0;

        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;

            int start = i;

            while (i < text.Length && !char.IsWhiteSpace(text[i]))
                i++;

            if (i - start > 1 && (text[start] == '+' || text[start] == '-'))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Removes a trailing <c>+</c>/<c>-</c> from the free segment before a quote when it begins a
    /// token (it is the whole segment, or preceded by whitespace), making it the qualifier of the
    /// quoted segment. A sign attached to a preceding word is part of that token and stays literal.
    /// </summary>
    private static QueryTermOperator TakeTrailingOperator(ref ReadOnlySpan<char> segment)
    {
        if (segment.Length == 0)
            return QueryTermOperator.None;

        char last = segment[^1];

        if (last is not ('+' or '-'))
            return QueryTermOperator.None;

        if (segment.Length > 1 && !char.IsWhiteSpace(segment[^2]))
            return QueryTermOperator.None;

        segment = segment[..^1];
        return last == '+' ? QueryTermOperator.Required : QueryTermOperator.Prohibited;
    }

    /// <summary>
    /// Takes the qualifier a quoted segment carries: a trailing <c>[sign]field:</c> token
    /// (<c>title:"..."</c>, <c>+title:"..."</c>, <c>-title:"..."</c>) or a bare trailing
    /// <c>+</c>/<c>-</c>. The prefix has to touch the quote and begin the token, and the name has
    /// to be known; otherwise the segment is left for the tokenizer and the quote is just a quote.
    /// </summary>
    private static QueryTermOperator TakeTrailingScope(
        ref ReadOnlySpan<char> segment,
        IReadOnlySet<string>? fields,
        out string? field)
    {
        field = null;

        if (fields is { Count: > 0 } && segment.Length > 1 && segment[^1] == ':')
        {
            int start = segment.Length - 1;

            while (start > 0 && !char.IsWhiteSpace(segment[start - 1]))
                start--;

            var token = segment[start..];
            int nameStart = 0;
            var scopeOperator = QueryTermOperator.None;

            if (token.Length > 2 && token[0] is '+' or '-')
            {
                scopeOperator = token[0] == '+' ? QueryTermOperator.Required : QueryTermOperator.Prohibited;
                nameStart = 1;
            }

            var name = token[nameStart..^1];

            if (name.Length > 0)
            {
                string candidate = name.ToString();

                if (fields.Contains(candidate))
                {
                    field = candidate;
                    segment = segment[..start];
                    return scopeOperator;
                }
            }
        }

        return TakeTrailingOperator(ref segment);
    }

    /// <summary>
    /// Appends one free-text (outside-quote) segment. With no leading <c>+</c>/<c>-</c> token and
    /// no field prefix it is exactly the pre-operator behavior — the whole segment is tokenized —
    /// so a query that uses neither syntax takes the same path it always did. Otherwise the
    /// segment is walked one whitespace-delimited token at a time, so a <c>-</c> inside a word or
    /// a colon that does not separate a known field stays literal.
    /// </summary>
    private static void AppendFreeSegment(
        List<string> freeTerms,
        List<string> requiredTerms,
        List<string> prohibitedTerms,
        List<QueryExpansion> expansions,
        ScopeState scope,
        ReadOnlySpan<char> segment,
        ITokenizer tokenizer,
        IReadOnlySet<string>? fields)
    {
        if (segment.Length == 0)
            return;

        if (!ContainsOperatorToken(segment) && !ContainsFieldScope(segment, fields))
        {
            AppendFree(freeTerms, expansions, segment, tokenizer, field: null);
            return;
        }

        int i = 0;

        while (i < segment.Length)
        {
            while (i < segment.Length && char.IsWhiteSpace(segment[i]))
                i++;

            int start = i;

            while (i < segment.Length && !char.IsWhiteSpace(segment[i]))
                i++;

            if (start == i)
                break;

            var token = segment[start..i];
            var termOperator = QueryTermOperator.None;

            if (token.Length > 1 && token[0] is '+' or '-')
            {
                termOperator = token[0] == '+' ? QueryTermOperator.Required : QueryTermOperator.Prohibited;
                token = token[1..];
            }

            if (TrySplitFieldScope(token, fields, out string field, out var scopedToken))
            {
                var literal = new List<string>(4);
                int firstScopedExpansion = expansions.Count;
                AppendFree(literal, expansions, scopedToken, tokenizer, field);
                ApplyOperator(expansions, firstScopedExpansion, termOperator);
                scope.Add(literal, termOperator, field);
                continue;
            }

            var target = termOperator switch
            {
                QueryTermOperator.Required => requiredTerms,
                QueryTermOperator.Prohibited => prohibitedTerms,
                _ => freeTerms,
            };

            int firstExpansion = expansions.Count;
            AppendFree(target, expansions, token, tokenizer, field: null);
            ApplyOperator(expansions, firstExpansion, termOperator);
        }
    }

    /// <summary>
    /// Stamps the trailing <c>+</c>/<c>-</c> onto the expansion atoms one token produced. A sign
    /// with nothing after it stamps nothing: the expansions were already made without one.
    /// </summary>
    private static void ApplyOperator(
        List<QueryExpansion> expansions, int firstExpansion, QueryTermOperator termOperator)
    {
        if (termOperator == QueryTermOperator.None)
            return;

        for (int e = firstExpansion; e < expansions.Count; e++)
            expansions[e] = expansions[e] with { Operator = termOperator };
    }

    /// <summary>
    /// Appends one free-text atom: plain terms plus any <c>term*</c>/<c>term~N</c> expansion
    /// atoms, in reading order. Text between atoms tokenizes normally; an atom whose base does
    /// not tokenize to exactly one term falls back to plain text (the operators are separators
    /// for the tokenizer). <paramref name="field"/> is stamped onto every expansion atom so its
    /// resolved terms inherit the scope.
    /// </summary>
    private static void AppendFree(
        List<string> terms,
        List<QueryExpansion> expansions,
        ReadOnlySpan<char> segment,
        ITokenizer tokenizer,
        string? field)
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
                    maxEdits)
                {
                    Field = field,
                });
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
        ReadOnlySpan<char> segment,
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
    private static int ReadEditCount(ReadOnlySpan<char> segment, int start, out int maxEdits)
    {
        int edits = 0;
        int i = start;

        while (i < segment.Length && char.IsAsciiDigit(segment[i]))
        {
            edits = (edits * 10) + (segment[i] - '0');

            if (edits > 2)
                edits = 2;

            i++;
        }

        bool hasDigits = i > start;
        maxEdits = hasDigits ? edits : 1;
        return hasDigits ? i : start;
    }

    private static void AppendPhrase(
        QueryTermOperator termOperator,
        string? field,
        List<IReadOnlyList<string>> phrases,
        List<IReadOnlyList<string>> requiredPhrases,
        List<IReadOnlyList<string>> prohibitedPhrases,
        ScopeState scope,
        IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
            return;

        if (field is not null)
        {
            scope.AddPhrase(field, termOperator, terms);
            return;
        }

        var target = termOperator switch
        {
            QueryTermOperator.Required => requiredPhrases,
            QueryTermOperator.Prohibited => prohibitedPhrases,
            _ => phrases,
        };

        target.Add(terms);
    }

    /// <summary>
    /// Whether any whitespace-delimited token begins with <c>name:</c> for a known name and has
    /// something after the colon — the cheap test that keeps a colon that is ordinary text on the
    /// plain path.
    /// </summary>
    private static bool ContainsFieldScope(ReadOnlySpan<char> text, IReadOnlySet<string>? fields)
    {
        if (fields is not { Count: > 0 } || text.IndexOf(':') < 0)
            return false;

        int i = 0;

        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;

            int start = i;

            while (i < text.Length && !char.IsWhiteSpace(text[i]))
                i++;

            if (start == i)
                break;

            var token = text[start..i];

            if (token.Length > 1 && token[0] is '+' or '-')
                token = token[1..];

            if (TrySplitFieldScope(token, fields, out _, out _))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Splits <c>name:rest</c> into a known field and the rest of the token. The name is the text
    /// before the first colon; it must be known, and the rest must be non-empty and must not start
    /// with a sign (a sign is an operator only at the start of a token, never after a scope).
    /// </summary>
    private static bool TrySplitFieldScope(
        ReadOnlySpan<char> token,
        IReadOnlySet<string>? fields,
        out string field,
        out ReadOnlySpan<char> rest)
    {
        field = string.Empty;
        rest = token;

        if (fields is not { Count: > 0 })
            return false;

        int colon = token.IndexOf(':');

        if (colon <= 0 || colon == token.Length - 1)
            return false;

        if (token[colon + 1] is '+' or '-')
            return false;

        string name = token[..colon].ToString();

        if (!fields.Contains(name))
            return false;

        field = name;
        rest = token[(colon + 1)..];
        return true;
    }

    /// <summary>
    /// The field-scoped atoms one parse collected, kept together so the parsing helpers pass one
    /// object instead of six lists.
    /// </summary>
    private sealed class ScopeState
    {
        /// <summary>Scoped terms that score, including required ones and phrase terms.</summary>
        public List<FieldScopedTerm> Scoring { get; } = [];

        /// <summary>Scoped terms written with <c>+</c>.</summary>
        public List<FieldScopedTerm> Required { get; } = [];

        /// <summary>Scoped terms written with <c>-</c>.</summary>
        public List<FieldScopedTerm> Prohibited { get; } = [];

        public List<FieldScopedPhrase> Phrases { get; } = [];

        public List<FieldScopedPhrase> RequiredPhrases { get; } = [];

        public List<FieldScopedPhrase> ProhibitedPhrases { get; } = [];

        /// <summary>
        /// Records the terms one scoped token produced: prohibited ones gate only, required ones
        /// gate and score, unqualified ones score.
        /// </summary>
        public void Add(IReadOnlyList<string> terms, QueryTermOperator termOperator, string field)
        {
            for (int i = 0; i < terms.Count; i++)
            {
                var term = new FieldScopedTerm(field, terms[i]);

                if (termOperator != QueryTermOperator.Prohibited)
                    Scoring.Add(term);

                if (termOperator == QueryTermOperator.Required)
                    Required.Add(term);
                else if (termOperator == QueryTermOperator.Prohibited)
                    Prohibited.Add(term);
            }
        }

        public void AddPhrase(string field, QueryTermOperator termOperator, IReadOnlyList<string> terms)
        {
            // A prohibited phrase contributes no score; the others score term by term, exactly as
            // an unscoped phrase does.
            if (termOperator != QueryTermOperator.Prohibited)
            {
                for (int i = 0; i < terms.Count; i++)
                    Scoring.Add(new FieldScopedTerm(field, terms[i]));
            }

            var phrase = new FieldScopedPhrase(field, terms) { Operator = termOperator };
            var target = termOperator switch
            {
                QueryTermOperator.Required => RequiredPhrases,
                QueryTermOperator.Prohibited => ProhibitedPhrases,
                _ => Phrases,
            };

            target.Add(phrase);
        }
    }

    private static void AppendRawPhrase(List<string> phrases, ReadOnlySpan<char> interior)
    {
        if (interior.IsWhiteSpace())
            return;

        // Mirror Parse's rule with the default tokenizer as a proxy for "will any backend
        // produce a token from this": punctuation-only interiors impose no constraint.
        if (Tokenizer.Default.Tokenize(interior).Count == 0)
            return;

        phrases.Add(interior.ToString());
    }
}
