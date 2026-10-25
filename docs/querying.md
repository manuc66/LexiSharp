---
title: "Querying: filters, facets, highlighting"
nav_order: 4
description: >-
  Metadata filters, pagination, phrase queries, highlighting, prefix and fuzzy
  terms, synonyms and facets, through the span-first API.
---

# Querying: filters, facets, highlighting

The query side: what a query string may contain, and what comes back with the results.
Every feature below is honored by the stock engine; the SQL backends support a subset and
say so in the notes (`NotSupportedException` rather than a silent no-op — see
[Backends](backends.md)).

- **Metadata filters** — declarative, AND-composed predicates over document fields
  (`equal`, `not-equal`, `contains`, numeric-or-ordinal greater/less than) applied before
  scoring. See [Metadata filters](#metadata-filters).
- **Pagination** — `SearchOptions.Offset` cuts any window `[Offset, Offset + Limit)` of the
  ranking, honored by the stock engine, the boost/rerank decorators, the hybrid merger and
  every SQL backend.
- **Phrase queries** — a double-quoted segment must appear at consecutive positions while
  the free terms around it keep scoring. See [Phrase queries](#phrase-queries).
- **Required & prohibited terms** — a leading `+` on a whitespace-delimited token requires it,
  a leading `-` excludes every document containing it. See
  [Required and prohibited terms](#required-and-prohibited-terms).
- **Field scopes** — `title:term` and `title:"a phrase"` match in one field only, and score from
  that field's statistics; `+`/`-` combine with them. A prefix is a field only when the name is a
  field of the corpus. See [Field scopes](#field-scopes).
- **Highlighting** — `TextHighlighter` wraps query matches in the original text or returns
  word-snapped snippets, driven by the tokenizer's span mode. See
  [Highlighting](#highlighting).
- **Prefix & fuzzy** — `neural*` and `catt~` / `catt~N` expand against the index vocabulary
  at search time, with an optional selective mode. See
  [Prefix & fuzzy queries](#prefix-and-fuzzy-queries).
- **Synonyms** — `SynonymMap` applies one-way rewrites and bidirectional groups to free
  query terms, one level deep. See [Synonyms](#synonyms).
- **Facets** — `SearchWithFacets` returns the ranked page plus value counts per requested
  field over the whole match set, and `SearchWithNumericFacets` adds half-open numeric ranges
  or a histogram. See [Facets](#facets).
- **Span-first API** — every text entry point has a `ReadOnlySpan<char>` overload, so a
  query in a buffer need not be copied into a `string`. See
  [Span-first API](#span-first-api).

## Metadata filters

Gate the corpus with structured predicates over `SearchDocument.Fields` — every filter must
hold (AND), and filtering happens before scoring:

```csharp
var options = new SearchOptions(
    Limit: 10,
    Filters:
    [
        new MetadataFilter("kind", MetadataFilterOperator.Equal, "article"),
        new MetadataFilter("year", MetadataFilterOperator.GreaterThan, "2023"),
        new MetadataFilter("tags", MetadataFilterOperator.Contains, "nlp"),
    ]);

var results = engine.Search("vector search", options);
```

Comparisons are culture-invariant; greater/less-than go numeric when both sides parse as
numbers, otherwise ordinal. Documents missing a field fail everything except `NotEqual`.
Every backend honors the same contract: the in-memory engines evaluate the predicate before
scoring, and the PostgreSQL backends (lexical, fuzzy, vector, sparse) push it down as a
parameterized predicate over the `fields jsonb` column. ParadeDB's custom planner rejects those
predicate shapes next to its BM25 operator, so it filters in C# over the whole match set instead
(same semantics, no pushdown).

## Phrase queries

Quote a segment of the query to require its terms at consecutive document positions:

```csharp
var results = engine.Search("neural \"machine learning\"", new SearchOptions(Limit: 10));
```

Parsing happens before tokenization (`QueryParser`): double quotes are otherwise an ordinary
separator for the tokenizer. In a mixed query the phrase is a hard corpus gate while the free
terms around it only contribute to scoring — a document that matches the phrase comes back
even if it lacks every free term, and a document that only has the free terms never does.
Several quoted segments are AND-ed. A query made solely of empty quotes matches nothing.
Phrase checks assume a plain token stream: n-gram tokenizers emit overlapping tokens and
break the consecutive-position guarantee.

The SQL backends honor the same syntax natively: PostgreSQL through
`websearch_to_tsquery`, ParadeDB through the `###` phrase operator (on that backend only
phrases shape the match set when quotes are present — free terms stay out of `WHERE`, exactly
mirroring the stock engine's scoring-only role for them). An engine without phrase support
(fuzzy, vector, sparse) rejects a quoted query with `NotSupportedException` rather than
ignoring the quotes.

## Required and prohibited terms

Put `+` or `-` immediately before a whitespace-delimited token to require it or exclude it.
The token may be a word, a quoted phrase, or a prefix/fuzzy atom:

```csharp
engine.Search("+apple");                 // only documents containing "apple"
engine.Search("apple -tart");            // "apple" scores; a document containing "tart" is out
engine.Search("+apple -tart");           // both gates
engine.Search("+\"machine learning\"");  // the quoted phrase must be adjacent
engine.Search("-catt~2");                // no near-variant of "catt" may occur
engine.Search("+neural*");               // at least one term starting with "neural" must occur
```

**Grammar.** The operator is the first character of a whitespace-delimited token, and it must
touch what it qualifies: `+term`, `-term*`, `-"a phrase"`. A `-` anywhere else is ordinary
text, so `e-mail`, `well-being` and `foo-bar` tokenize exactly as they do without the query
language, and `+ machine` is a bare `+` token (which tokenizes to nothing) followed by an
unqualified term. When a token starts with several operator characters the first one decides:
`++a` and `+-a` mean `+a`, `--a` and `-+a` mean `-a`. A qualifier on a segment that produces no
terms — `-""`, or `+a` where the tokenizer drops the single character — is dropped with that
segment and imposes no constraint; a query whose only content is such a token has no term to
match, so it returns nothing, the same as any other query with no terms. A token that tokenizes
to several terms is used whole: `+foo-bar` requires both `foo` and `bar`, and `-foo-bar` excludes
a document containing either.

**What the operators do.** Both are gates checked before any relevance math:

- A **required** atom must be present: every literal term of the token, at least one term
  matched by a `+` prefix/fuzzy expansion, or the whole phrase for `+"..."`. A required term
  then scores exactly as the same term written without `+`, so `+apple +red` ranks on both
  terms, not on a constant.
- A **prohibited** atom must be absent: any document where it occurs is dropped whatever it
  would have scored, and its terms contribute nothing to the score.
- A query made **only of prohibited atoms** is the complement of its gate: every surviving
  document scores `1.0`, so the configured tie-break — not relevance — orders the page. A
  `MinimumScore` above `1` removes them all, which is the explicit way to say "I wanted a
  ranking signal, not a filter".
- A query made **only of required atoms** with no free term scores on exactly those terms.

Required and prohibited words are taken literally: the synonym map rewrites free query terms
only, and never a `+`/`-` term. `QueryParser.Parse` exposes the split as
`RequiredTerms`/`ProhibitedTerms`, `RequiredPhrases`/`ProhibitedPhrases` and
`QueryExpansion.Operator`; `ParsedQuery.AllTerms` — the scoring input, and what highlighting
uses — contains the required terms and omits the prohibited ones.

`SearchOptions.ParseQuerySyntax` decides whether any of this is read. It defaults to `true`.
With it set to `false`, `-term` is literal text in which the tokenizer treats `-` as a
separator, so the query searches `term`. That is the path for a corpus of prose that contains
leading hyphens, in the same way it is the path for prose that contains quotation marks.

### Which engines honor the operators

`RankedTextSearchEngine` implements both. Every other backend declares the features it
supports through `IQuerySyntaxSupport.SupportedQueryFeatures`, and using an operator a backend
does not declare raises `NotSupportedException` before any query runs — never a silent
retrieval of documents containing the term that was supposed to be excluded.
`ParseQuerySyntax: false` opts out of that check along with the rest of the query language: the
raw text is then that backend's own grammar to read.

| Engine | Query syntax |
|---|---|
| `RankedTextSearchEngine` | phrases, prefix/fuzzy, required and prohibited terms; field scopes when its scorer and index declare them (see [Which engines honor field scopes](#which-engines-honor-field-scopes)) |
| `PostgresTextSearchEngine`, `ParadeDBTextSearchEngine` | phrases |
| `PostgresFuzzySearchEngine`, `PostgresVectorSearchEngine`, `PostgresSparseSearchEngine`, `InMemoryVectorSearchEngine`, `SparseTextSearchEngine` | plain queries only |

The SQL backends are declared unsupported rather than approximated: the lexical engine forwards
its query text to `websearch_to_tsquery`, which has its own grammar, and the ParadeDB engine
forwards it to `pg_search`; neither would honor this grammar on the terms this library
tokenizes. A `+`/`-` query against them is refused, and the message names the missing feature.

## Field scopes

Put a field name and a colon immediately before an atom to match that atom in one field only:

```csharp
engine.Search("title:obsidian");             // "obsidian" must be in the title
engine.Search("title:\"machine learning\""); // the phrase, at consecutive title positions
engine.Search("+title:obsidian");            // required: the document must have it in the title
engine.Search("-title:draft");               // excluded if the title contains "draft"
engine.Search("title:obsid*");               // prefix expansion, resolved inside the title
```

A scoped atom is the same atom with a field attached. It **scores from that field's statistics**:
`tf` is the term's frequency in the field, the length correction uses the field's length and
corpus average, and the document frequency is the field's own (`FieldDocumentFrequency`). The
inverse document frequency keeps the corpus-wide document count `N`, so a term that appears in
few titles is worth more than the same term appearing in many bodies — which is what asking for
title evidence means. The scorer's configured weight for the field still applies: a scoped term
is field evidence, and the weight says what that evidence is worth in this corpus.

A scoped term joins the score of the unscoped terms rather than replacing them, so
`title:obsidian notes` ranks on the title match plus the free term. As with an unscoped free
term, a scoped one is **not** a hard gate: a document that matches only the free term still comes
back. It behaves as a gate when it is the only positive content (nothing else scores), and it is
one explicitly with `+`.

**Grammar.** The prefix must be the first thing in a whitespace-delimited token after an optional
leading `+`/`-`, it must name a field the query knows about (see below), and it must touch what it
scopes: `title:term`, `title:"a phrase"`, `+title:term`, `-title:"a phrase"`, `title:term*`,
`title:term~2`. `title: term`, a bare `title:`, `title:-term` and `"title:term"` (inside quotes)
are ordinary text. Field names are matched exactly as the index spells them — ordinal, so
`Title:term` is not `title:term` unless the index has a `Title` field. The name is the text before
the **first** colon, so a field whose own name contains a colon cannot be addressed by a prefix (a
name with whitespace is equally out of reach); `Fields` will report it, and it remains searchable
unscoped. A scoped token that tokenizes to no term is dropped with its scope, exactly as a vacuous
`+` atom is.

**The colon is the dangerous character, and the rule is the field vocabulary.** A name before a
colon is a scope only when it is a *known field*: the corpus's own `IReadOnlyTextIndex.Fields` by
default, or `SearchOptions.KnownFields` when the caller declares the vocabulary. So `http://x`,
`12:30` and `key:value` remain the tokens they always were on a corpus whose fields are, say,
`title` and `body`. If a corpus really has a field called `key`, then `key:value` **is** a scope —
the rule is the name, not the shape — and a field named `http` would make `http://x` one. That is
why the vocabulary is explicit rather than guessed from `\w+:`.

A scoped phrase must appear at **consecutive positions inside its field**. Positions are read
through `IFieldStatisticsIndex.FieldTermPositions`, which returns the term's occurrences inside
the field's own run of the document position space; a phrase satisfied in the body cannot stand in
for one scoped to the title, even when both words are also somewhere in the title.

**Unknown names fail loudly.** A prefix that parses as a scope but names a field the index does
not have raises `NotSupportedException` naming the field and the index's fields, rather than
returning nothing — a typo and a corpus with no match are different answers. A name the caller
did not declare stays text, as above.

**Escape hatch.** `ParseQuerySyntax: false` turns the whole grammar off, colons included: the
query is then exactly `ITokenizer.Tokenize(query)`, so `title:obsidian` searches `title` and
`obsidian` as two ordinary terms. That is the setting for a corpus of prose in which a colon
prefix is a label rather than a query.

### Which engines honor field scopes

`RankedTextSearchEngine` honors them when **both** parts can: its scorer implements
`IFieldScopedScorer` (the built-in `Bm25FScorer` does) and its index implements
`IFieldStatisticsIndex` (`InMemoryTextIndex`, and the decorators that wrap one, do). The engine
declares `QueryFeatures.FieldScopes` in exactly that configuration and refuses a scoped query
otherwise, before scoring anything, naming the missing capability. An index that tracks the
statistics but not per-field positions answers a scoped single term and throws
`NotSupportedException` for a scoped phrase; `InMemoryTextIndex` answers both.

| Engine | Field scopes |
|---|---|
| `RankedTextSearchEngine` + `Bm25FScorer` over an `IFieldStatisticsIndex` | yes |
| `RankedTextSearchEngine` + any other scorer, or over an index without field statistics | refused |
| `PostgresTextSearchEngine`, `ParadeDBTextSearchEngine` | refused when `KnownFields` is set; otherwise the prefix is text |
| `PostgresFuzzySearchEngine`, `PostgresVectorSearchEngine`, `PostgresSparseSearchEngine`, `InMemoryVectorSearchEngine`, `SparseTextSearchEngine` | refused when `KnownFields` is set; otherwise the prefix is text |

The SQL backends have no field model in their table (the text is one `tsvector`/`pg_search`
column), so they do not map the syntax; a caller who sets `SearchOptions.KnownFields` gets a
refusal that names the missing feature instead of a query forwarded as text. Without that set the
engine has no way to distinguish `title:foo` from a word before a colon — which is the colon rule's
whole point — so the text goes to the backend's own grammar unchanged.

`Explain` refuses a field-scoped query (`NotSupportedException`): a `ScoreExplanation` has no field
per contribution, and a total that silently omitted the scoped part would not be the score the
search ranked on. `ParsedQuery.ScopedTerms`, `ScopedPhrases` and their required/prohibited
siblings expose the split; `FieldScopedTerm` and `FieldScopedPhrase` are the shapes.

## Highlighting

Mark where a query matched inside a document — same normalization as the index, so hits land
on token boundaries even when the source text differs in case or accents:

```csharp
using LexiSharp.Highlighting;

var terms = Tokenizer.Default.Tokenize(query);   // or QueryParser.Parse(query, tokenizer).AllTerms

string marked = TextHighlighter.HighlightFull(document.Text, terms, Tokenizer.Default);
// "The <em>quick</em> brown fox jumps over the lazy dog"

IReadOnlyList<HighlightSnippet> snippets = TextHighlighter.Highlight(
    document.Text, terms, Tokenizer.Default,
    new HighlightOptions { MaxSnippets = 2, Padding = 30 });
```

Matching goes through the tokenizer's span mode (`ISpanTokenizer.TokenizeWithSpans`, built
into `Tokenizer`): each normalized term carries its `[Start, Length)` offsets into the source,
so the query must be tokenized with the same tokenizer. Nearby matches cluster into one
snippet, windows snap outward to word boundaries, and overlapping ranges (n-gram tokenizers)
merge before tagging.

## Prefix and fuzzy queries

Suffix a free-text atom to expand it against the index vocabulary at search time:

```csharp
engine.Search("neural*");    // every indexed term starting with "neural"
engine.Search("catt~");      // within 1 edit: "cat", "cats", "catt", ...
engine.Search("catt~2");     // within 2 edits (count clamped to 0–2)
```

Expansion is a stock-engine feature: the atom's base is tokenized first, then matched against
an `IVocabularyIndex` (`InMemoryTextIndex` implements it), keeping at most 64 terms per atom —
highest document frequency first, then ordinal order. An index without vocabulary support
falls back to the atom's literal base term, i.e. the behavior of a query without operators.
Operators are only recognized when suffixed to word characters, never inside quoted phrases
(a `"machine*"` phrase stays literal). They are a LexiSharp query syntax, so an engine that does
not interpret them rejects such a query with `NotSupportedException` instead of silently treating
the operator as plain text — `IQuerySyntaxSupport.SupportedQueryFeatures` exposes each engine's
supported matrix (`RankedTextSearchEngine`: phrases, expansions, required and prohibited terms;
PostgreSQL lexical and ParadeDB: phrases; fuzzy/vector/sparse: plain queries only). See
[Which engines honor the operators](#which-engines-honor-the-operators).

## Synonyms

Register synonym edges once, then every free query term pulls in its direct synonyms:

```csharp
using LexiSharp.Linguistics;

var synonyms = new SynonymMap()
    .Add("car", "auto")                       // one-way: "car" also searches "auto"
    .AddEquivalent("auto", "automobile");     // bidirectional group

ITextSearchEngine engine = new RankedTextSearchEngine(
    new InMemoryTextIndex(),
    new Bm25Scorer(),
    synonyms: synonyms);
```

Entries are tokenized with the engine's tokenizer at construction and each must reduce to
exactly one term (otherwise the constructor throws). Expansion is one level deep and
non-transitive — a synonym's own synonyms are never pulled in — and applies to free terms
only: quoted phrases stay literal. Like the prefix/fuzzy operators, this is a stock-engine
feature; the SQL backends do not currently rewrite queries.

## Facets

Get value counts for UI refinements alongside the ranked page:

```csharp
using LexiSharp.Core;

IFacetedSearchEngine engine = new RankedTextSearchEngine(index, new Bm25Scorer());

FacetedSearchResult page = engine.SearchWithFacets(
    "fast car",
    new SearchOptions(Limit: 10),
    facetFields: ["kind", "lang"]);

foreach (var bucket in page.Buckets)
    foreach (var value in bucket.Values)          // count desc, then value ordinal
        Console.WriteLine($"{bucket.Field}={value.Value}: {value.Count}");
```

`Results` is identical to `Search` for the same arguments. Counts cover every document that
passes the metadata filters, the phrase gates and the score thresholds — the whole match set —
independently of `Offset`/`Limit`, which only cut `Results`. A document missing a field does
not count for it (faceting reads `Fields` only, not `Category`), and fields no matching
document carries are omitted from `Buckets`. Currently stock-engine only.

### Numeric ranges and histograms

String facets count distinct values; a price or a year needs buckets. `SearchWithNumericFacets`
takes the same search and the same string facets, plus numeric requests over `Fields`:

```csharp
FacetedSearchResult page = engine.SearchWithNumericFacets(
    "fast car",
    new SearchOptions(Limit: 10),
    facetFields: ["kind"],
    numericFacets:
    [
        NumericFacetRequest.Ranges("price", [0, 10, 50, 100]),  // [0,10) [10,50) [50,100)
        NumericFacetRequest.Histogram("year", 5),               // 5 buckets, min..max of the matches
    ]);

foreach (var bucket in page.NumericBuckets)
    foreach (var range in bucket.Ranges)
        Console.WriteLine($"{bucket.Field} [{range.From}, {range.To}) = {range.Count}");
```

**The boundary rule.** A range includes its lower boundary and excludes its upper one: a value
equal to `From` is counted, a value equal to `To` is not — it belongs to the range that starts
there, if the boundaries put one there. Values below the first boundary or at or above the last
one are in no range, which is what choosing the boundaries means; the reported ranges show what
is covered.

**Histograms** derive their boundaries from the match set — the smallest and largest parseable
values — and split that span into equal-width buckets. The last bucket is closed at the top
(`UpperInclusive` is `true`, `[from, to]`) so the largest value is counted; every other bucket,
and every explicit range, is `[from, to)`. When every value is identical the width is zero and
the facet reports one bucket whose `From` equals its `To`, counting every value. A histogram of
`n` buckets reports `n` ranges whenever at least one value parses; a bucket count larger than the
number of distinct values leaves the extra ranges empty rather than dropping them.

**Parsing.** A value is read with `double.TryParse`, `NumberStyles.Any`, invariant culture — the
same parse `MetadataFilter` uses for its numeric comparisons — and must be finite. A document
missing the field does not count, exactly as for the string facets. A value that does not parse
(`n/a`, `NaN`, `Infinity`) is **not** placed in a range: it is counted in
`NumericFacetBucket.UnparseableCount` instead, and the string facet on the same field still
lists it. `MetadataFilter` falls back to an ordinal string comparison for such a value, because
a filter still has to answer `year > 2023` on a field holding the odd non-number; a range facet
cannot, because its edges are numbers and a non-number has no position among them.

Counts cover the whole match set independently of `Offset`/`Limit`, and `Results` is identical
to `Search`. String facets are unchanged — a `SearchWithFacets` call leaves `NumericBuckets`
empty — and `SearchWithNumericFacets` is opt-in: an engine that implements the string facets but
not this one throws `NotSupportedException` rather than returning buckets that look like a
match set where nothing parsed. Currently stock-engine only.

## Span-first API

The text entry points accept `ReadOnlySpan<char>`, so a query already living in a buffer need
not be copied into a `string` first:

```csharp
ReadOnlySpan<char> query = buffer.AsSpan(offset, length);

var parsed = QueryParser.Parse(query, Tokenizer.Default);
IReadOnlyList<SearchResult> hits = engine.Search(query, new SearchOptions(Limit: 10));
FacetedSearchResult page = engine.SearchWithFacets(query, facetFields: ["kind"]);
ScoreExplanation? why = engine.Explain("doc-1", query);
```

Every overload is equivalent to its `string` counterpart. `Tokenizer` runs the same pipeline
directly over the span (SIMD ASCII runs, rune decoding only on the non-ASCII path); other
implementers fall back to the default interface method, which copies the span and forwards. A
`null` literal still binds to the `string` overload, so the span path is null-free.
