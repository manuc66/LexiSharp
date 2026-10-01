---
title: Building an inverted index
nav_order: 3
description: >-
  The in-memory inverted index and its statistics, named text fields, tokenization
  and stemming, and binary persistence of a whole corpus.
---

# Building an inverted index

What the core does with your text before anything is ranked: an inverted index over
normalized terms, corpus statistics, optional named fields per document, a configurable
tokenizer, and binary persistence.

- **`InMemoryTextIndex`** — an inverted index with term positions, document frequencies,
  corpus statistics and incremental `Add`/`Remove`, plus a statistics snapshot
  (`GetStatistics`: documents, vocabulary, tokens, average length, vocabulary richness).
- **Named fields** — a document's `TextFields` are indexed as named fields, and the index
  answers per-field term frequency, field length, average field length and field document
  frequency. The **flat** statistics stay the union of every field, so a plain BM25 query
  still finds a term that only occurs in a title, and a single-field document is
  unaffected. See [Multi-field documents](#multi-field-documents).
- **Configurable tokenizer** — Unicode NFKD normalization and diacritics removal,
  lowercasing, optional stop-word removal, optional n-grams, and a pluggable `IStemmer`
  seam, with `PorterStemmer` (English, no dependency, opt-in) shipped in the core. See
  [Tokenizer customization](#tokenizer-customization).
- **Persistence** — `LexiSharp.MessagePack` saves and reloads a whole corpus, tokenizer
  configuration included. See
  [Index persistence](indexing.md#index-persistence-lexisharpmessagepack).

## Multi-field documents

A document is not only one blob of text. Give it named text sections and the index tracks each one
separately, which is what a field-weighted ranking needs:

```csharp
using LexiSharp.Core;
using LexiSharp.Indexing;

var index = new InMemoryTextIndex();

index.Index(new[]
{
    new SearchDocument(
        Id: "1",
        Text: "the article body goes on at some length about indexing",
        TextFields: new Dictionary<string, string>
        {
            ["title"] = "a guide to search ranking",
            ["summary"] = "an introduction",
        }),
});
```

Two views of the same data, and the distinction is the whole design:

| | `TermFrequency` / `DocumentLength` / `DocumentFrequency` | `FieldTermFrequency` / `FieldLength` / `FieldDocumentFrequency` |
|---|---|---|
| Sees | the **union** of every field | one named field |
| Answers | "does this document match, and how long is it" | "how much of the match is in the title" |
| `b` normalizes against | the whole document | the field, against `AverageFieldLength(field)` |

Because the flat view is the union, **a field-only term is still findable**. With the document above,
a plain `new Bm25Scorer()` over `index` matches `ranking` even though it never appears in
`SearchDocument.Text` — no separate index or query path is needed to make a description searchable.
A document with no `TextFields` indexes exactly as before.

```csharp
// The flat view, field-unaware.
index.TermFrequency("1", "ranking");            // 1
index.DocumentLength("1");                      // body + title + summary

// The per-field view, for a field-weighted scorer to be written against.
index.Fields;                                    // ["", "summary", "title"] — default first, then ordinal
index.FieldTermFrequency("1", "title", "guide"); // 1
index.FieldLength("1", "title");                 // title tokens only
index.AverageFieldLength("title");               // over every document declaring that field
index.FieldDocumentFrequency("title", "ranking");
```

`TextFields.Default` is the empty string — it names the document's main `Text`, and it is the
reserved default field. `Fields` always lists it first, then the named ones in ordinal order, so the
sequence is stable and safe to assert on. A blank (whitespace-only) field name is rejected, since it
could not be told apart from the default.

**A quoted phrase matches inside one field and never bridges two.** Field tokens are indexed after
the main text with a gap, so `"body guide"` cannot match across the boundary while `"a guide"`
still matches inside the title.

**On the typed facade**, `TextFields` defaults to the document's own `SearchDocument.TextFields`
when you index `SearchDocument` directly, and is otherwise yours to set:

```csharp
var index = new LexiSharpIndex<Product>(o =>
{
    o.Id = p => p.Sku;
    o.Text = p => p.Description;
    o.TextFields = p => new Dictionary<string, string> { ["name"] = p.Name };
});

// index.TextIndex is the ITextIndex behind the searches, so a field-aware scorer can be built
// over the very same index instead of a second one you have to keep in step by hand.
```

**What this is not.** No field-weighted scorer ships yet: the statistics BM25F needs are here, and
the scorer that consumes them is not. `HasFieldStatistics` is `false` on an index that does not
track fields, and the per-field members then throw `NotSupportedException` naming the index — they
deliberately do not return `0`, which would make a field-weighted ranking quietly wrong with no
error to show for it. Only `InMemoryTextIndex` and `ExpansionTextIndex` track fields; the PostgreSQL
and ParadeDB backends implement `ITextSearchEngine` and are unaffected by the `ITextIndex` additions.

**A field present but empty still counts.** A document that declares a title tokenizing to nothing
is included in `AverageFieldLength(title)`, so the average reflects the documents that *have* the
field rather than only the ones that filled it.

**Loading a title from a source.** `MarkdownLoadOptions.TextFieldNames` names the front-matter keys
to promote to text fields, and `LoadedDocument.ToSearchDocument()` carries them through:

```csharp
var documents = MarkdownLoader.LoadDirectory(
    "notes/", new MarkdownLoadOptions { TextFieldNames = ["title"] });
```

It defaults to none, on purpose: front matter is mostly metadata — a date, a status, an author id —
and promoting all of it would make those values match queries. A promoted key is **in addition to**
the document field it already was, so the title stays filterable and facetable *and* becomes
searchable. On the reference corpus this is worth a lot to plain BM25: nDCG@5 goes from **0.8359 to
0.8751**, because before this a markdown document's title was not indexed at all. The committed
golden master was re-recorded in the same change, and `verify` fails loudly without it.

## Context enrichment

A chunk that only says *"the benefit rose 12%"* does not say of which company or which year — and
a search engine only matches the words it is given. `IChunkContextEnricher` is the seam for
rewriting a document's **searchable text before it is indexed** so the corpus that is scored can
carry context the document does not spell out, while the caller's original document stays what
results, highlighting and facets display:

```csharp
using LexiSharp.Core;
using LexiSharp.Indexing;

IChunkContextEnricher enricher = new FieldPrefixContextEnricher(["title", "year"]);

var index = new ContextEnrichingIndex(new InMemoryTextIndex(), enricher);

index.Add(new SearchDocument(
    Id: "1",
    Text: "the benefit rose 12%",
    Fields: new Dictionary<string, string> { ["title"] = "acme annual report", ["year"] = "2024" }));
```

`ContextEnrichingIndex` keeps **two texts per document**. What is *scored* is the enriched text:
the postings, term/document frequencies, document lengths and vocabulary below it all describe
`"acme annual report 2024 the benefit rose 12%"`. What is *displayed* is the caller's original:
`Documents`, `TryGetDocument` and every `SearchResult.Document` carry `"the benefit rose 12%"`
untouched. A search for `acme` reaches the chunk while the result still shows the source text.

The enricher is a consumer-provided seam, like `IEmbeddingProvider` and `ICrossEncoderScorer`:
`FieldPrefixContextEnricher` (above) is the deterministic, model-free case, and a context-injection
LLM is the other one. LexiSharp never runs such a model itself, and `Enrich` is synchronous — a
model-backed implementation blocks on its async work, the same trade-off the PostgreSQL engines
make. Enrichment runs once per `Add`/`Index`, never at search time; a failing enricher fails the
index operation, because indexing the raw text when enrichment was asked for would change what is
scored silently. An enricher that changes the document id is rejected.

On the typed facade, set it on the options:

```csharp
var index = new LexiSharpIndex<Product>(o =>
{
    o.Id = p => p.Sku;
    o.Text = p => p.Description;
    o.ContextEnricher = new FieldPrefixContextEnricher(["tags"]);
});
```

**What this is not.** It does not chunk: a `SearchDocument` is the unit of indexed text, and the
enricher rewrites each unit rather than splitting it. It does not change what is displayed: the
raw document is what highlighting runs over, so a term that exists only in the enriched text is
searchable but not highlighted. And it does not reach the embedding-backed engines
(`PostgresVectorSearchEngine`, the in-memory dense engine), which read `SearchDocument` directly
and do not share the `ITextIndex` this decorator wraps — enriching those corpora is the caller's
job at the embedding call site.

`ContextEnrichingIndex` composes with `ExpansionTextIndex`: wrapping one in the other applies
enrichment first and expansion second, so the expander sees the text the enricher produced.

## Tokenizer customization

```csharp
using LexiSharp.Linguistics;

var tokenizer = new Tokenizer(new TokenizerOptions
{
    RemoveStopWords = true,       // English list, or provide StopWords.Create(...)
    NGramMax = 2,                 // produce unigrams + bigrams
    Stemmer = new PorterStemmer(), // English stemming, shipped; null (default) = no stemming
});
```

`PorterStemmer` implements the frozen Porter algorithm (Porter, 1980) in the core package, with
no dependency: it stems only English, and only the ASCII lowercase terms the tokenizer produces.
It is **opt-in** — `Stemmer` stays `null` by default — because it is English-only (a default
would change terms for every other language) and because a stemmed index can only be reloaded
with the same stemmer. Conformance is pinned against the algorithm author's own reference
vocabulary (23,531 words). Porter describes the algorithm as "slightly inferior to the Snowball
English or Porter2 stemmer", and it over-stems by design (`relate` and `relational` both become
`relat`, `engine` becomes `engin`), so measure it on your own data before turning it on:

| BM25 nDCG@10, `LexiSharp.Eval` | no stemming | `--stem porter` | BEIR BM25 |
|---|---|---|---|
| NFCorpus (323 queries)              | 0.308 | **0.322** | 0.325 |
| SciFact (300 queries)               | 0.662 | **0.687** | 0.665 |
| ArguAna (1406 queries)              | 0.320 | 0.308 | 0.315 |

It helps on two corpora and is mixed on the third (ArguAna: BM25 0.320 → 0.308, QL 0.302 → 0.312),
so it stays opt-in: ArguAna's whole-argument
queries are nearly all content words, the case where over-stemming has most to lose. Full tables
for every config are in
[the eval harness README](https://github.com/manuc66/LexiSharp/blob/main/bench/LexiSharp.Eval/README.md); reproduce with
`dotnet run --project bench/LexiSharp.Eval -- --stem porter`. For another language, implement
`IStemmer` (or take Snowball) and pass it the same way.

## Index persistence (`LexiSharp.MessagePack`)

Save and reload an `InMemoryTextIndex` as compact, LZ4-compressed MessagePack binary —
documents (id, text, fields, category) **and** tokenizer configuration:

```csharp
// install once:  dotnet add package LexiSharp.MessagePack
using LexiSharp.MessagePack;

MessagePackTextIndexPersistence.Save(index, "corpus.bin");
var reloaded = MessagePackTextIndexPersistence.Load("corpus.bin"); // identical statistics, no re-indexing
```

A `Tokenizer` (stop words, n-grams, single-char terms) is reconstructed automatically. A custom
`ITokenizer` is not currently serialized: hand the same implementation to `Load` — a type-name
check protects against rebuilding with the wrong pipeline. Stemmed tokenizers likewise require
the original tokenizer at load time (stemmers are not currently serializable).

The same package persists a sparse engine through `MessagePackSparseIndexPersistence`: the
stored corpus is the documents **plus their learned weights**, so reloading bypasses the model —
only queries need the `ISparseEmbeddingProvider` again:

```csharp
MessagePackSparseIndexPersistence.Save(sparseEngine, "splade.bin");
var reloaded = MessagePackSparseIndexPersistence.Load("splade.bin", mySplade); // exact same search scores
```
