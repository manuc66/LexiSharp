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
- **Highlighting** — `TextHighlighter` wraps query matches in the original text or returns
  word-snapped snippets, driven by the tokenizer's span mode. See
  [Highlighting](#highlighting).
- **Prefix & fuzzy** — `neural*` and `catt~` / `catt~N` expand against the index vocabulary
  at search time, with an optional selective mode. See
  [Prefix & fuzzy queries](#prefix-and-fuzzy-queries).
- **Synonyms** — `SynonymMap` applies one-way rewrites and bidirectional groups to free
  query terms, one level deep. See [Synonyms](#synonyms).
- **Facets** — `SearchWithFacets` returns the ranked page plus value counts per requested
  field over the whole match set. See [Facets](#facets).
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
(fuzzy, vector, sparse) rejects a quoted query with `NotSupportedException` rather than ignoring
the quotes.

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
supported matrix (`RankedTextSearchEngine`: phrases + expansions; PostgreSQL lexical and
ParadeDB: phrases; fuzzy/vector/sparse: plain queries only).

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
