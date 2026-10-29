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
  seam, with `PorterStemmer` (English) and `FrenchStemmer` (French) shipped in the core,
  neither with a dependency, both opt-in. See
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

| | `TermFrequency` / `DocumentLength` / `DocumentFrequency` | `FieldTermFrequency` / `FieldLength` / `FieldDocumentFrequency` / `FieldTermPositions`, on `IFieldStatisticsIndex` |
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
index.FieldTermPositions("1", "title", "guide"); // [the position inside the title's own run]
```

`TextFields.Default` is the empty string — it names the document's main `Text`, and it is the
reserved default field. `Fields` always lists it first, then the named ones in ordinal order, so the
sequence is stable and safe to assert on. A blank (whitespace-only) field name is rejected, since it
could not be told apart from the default.

**A quoted phrase matches inside one field and never bridges two.** Field tokens are indexed after
the main text with a gap, so `"body guide"` cannot match across the boundary while `"a guide"`
still matches inside the title. A phrase scoped to a field (`title:"a guide"`) reads
`FieldTermPositions` instead of the document-wide positions, so a phrase satisfied in the body
cannot stand in for one asked for in the title even when both words also occur in the title.
`FieldTermPositions` has a default implementation that throws `NotSupportedException`: an index
written before the member existed keeps compiling and answering the counts, and a scoped phrase
over it fails by name rather than checking the wrong positions.

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

**What this is not.** The per-field statistics are a capability, not a universal surface: an index
that tracks them implements `IFieldStatisticsIndex`, and a field-weighted scorer
(`Bm25FScorer`) refuses a corpus that does not, by name — `NotSupportedException` naming the
index, not a silent `0`, which would make a field-weighted ranking quietly wrong with no error to
show for it. Only `InMemoryTextIndex` and the index decorators wrapping one
(`ExpansionTextIndex`, `ContextEnrichingIndex`) track fields; the PostgreSQL and ParadeDB backends
implement `ITextSearchEngine` and are unaffected by the `ITextIndex` additions.

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
`IStemmer` (or take Snowball) and pass it the same way — or use `FrenchStemmer`, below.

### French stemming and stop words

`FrenchStemmer` is the other stemmer shipped in the core, and it takes the same slot:

```csharp
using LexiSharp.Linguistics;

var tokenizer = new Tokenizer(new TokenizerOptions
{
    RemoveStopWords = true,
    StopWords = StopWords.French,
    Stemmer = new FrenchStemmer(),
});
```

It implements the Snowball French algorithm, written from the project's published source
(`algorithms/french.sbl`) and pinned against its published vocabulary: **every one of 21,653
reference words stems to its reference output**, word for word, in `FrenchStemmerTests`. That is
the same standard `PorterStemmer` is held to and is what makes the name honest — a stemmer that
cannot be checked against a reference can only claim to be *a* stemmer.

`StopWords.French` is the 154-word list published with that stemmer, verbatim including its
deliberate omissions: the source leaves `été`, `son`, `est`, `as` and `avions` out because each
is a homonym of an ordinary content word, and dropping them would remove content from an index to
remove function words. It is one list rather than a choice of two, because nothing in this
repository's French measurement depends on the list's size the way `StopWords.EnglishFunction`
does.

Two things worth knowing before enabling either:

- **It is not idempotent.** Stemming the output again can change it — 1843 of the 21653 reference
  outputs are not fixed points of the algorithm (`abréviations` → `abrévi` → `abrev`), because a
  second pass runs rules the first pass put the word within reach of. That is a property of the
  algorithm, not a defect of this implementation, and it is why no idempotency test exists.
- **Its effect on retrieval is not measured here.** No French corpus is part of the evaluation
  harness, so nothing claims an nDCG figure for it. The English numbers above are English
  measurements and say nothing about French — measure it on your own data before turning it on.

Both are opt-in for the same reason `PorterStemmer` is: a default stemmer would silently change
terms for every corpus that never asked for one, and an index can only be reloaded with the
stemmer that produced it.

## Index persistence (`LexiSharp.MessagePack`)

Save and reload an `InMemoryTextIndex` as compact, LZ4-compressed MessagePack binary —
documents (id, text, fields, category) **and tokenizer configuration**:

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

### A segmented store: writes over an immutable index

`InMemoryTextIndex` holds the whole corpus in memory and mutates it in place. A search server that
must serve while it writes, or that must restart without re-tokenizing, needs the corpus on disk as
immutable segments — and the writes to land somewhere a reader can still see them. `SegmentedTextIndex`
is that: a directory of segments, a live overlay for the writes, and tombstones for the deletions.

```csharp
using LexiSharp.Indexing;

var store = new SegmentedTextIndex("./corpus");   // opens, or creates an empty store

store.Add(new SearchDocument("42", "the article body goes on about indexing"));
store.Remove("17");                                // a tombstone, not a rewrite

store.Commit();                                    // overlay + tombstones → one segment on disk
```

The page a query serves is the same page `InMemoryTextIndex` serves for the same corpus, bit for bit.
That is the property the whole design rests on, and it is what makes a segment a storage engine rather
than a second answer to the same question.

Four things a caller has to know, and each one is a consequence of a measurement:

- **A deletion removes a document from results and from `Count`, and touches nothing else.** The corpus
  statistics — `StatisticDocumentCount`, `AverageDocumentLength`, `DocumentFrequency` — describe the
  segments as they were written and stay frozen. This is not an oversight: BM25's inverse document
  frequency reads `N` and its length normalisation reads the average, so any statistic a deletion moved
  would move every score on the index. Measured on the same deletions over the same segment, a frozen
  statistic left 0 of 401 scores unchanged and a corrected one moved all 401. A merge is the point at
  which the statistics are rewritten and the scores move with them.
- **A tombstone costs 1.18× a head query at 10 ‰ deleted, and less from there.** The fold tests it per
  posting entry; the tax falls as the deletion rate rises, because a skipped posting is a `Record` the
  accumulator never does. `ShouldMerge()` reports when the count has passed 1 % of the live documents,
  which is where most of that tax sits. A merge of 40,000 documents — 100,000-term vocabulary, 124
  tokens each, writing a 108 MB segment — costs about 0.9 s and 126 MB allocated, so it is a batch
  operation a caller schedules.
- **A write is visible to the very next search, and survives a crash.** Adding a document is a
  tokenization and a dictionary write, not a segment write; an overlay of 1,000 documents reads at
  1.02× the same documents committed into a segment. Every write is also appended to a **write-ahead
  journal** (`journal.wal`, beside the segments) and replayed when the store opens, so a crash loses
  only the entries the flush policy has not forced to the device. `Commit()` is the second, stronger
  point: it writes one segment to a temporary file, **flushes it to the device**, then renames it into
  place. The file's contents therefore cannot be torn by a power loss; the directory entry is not
  fsynced (no portable API), so which of the rename and the cleanup the filesystem keeps is its own
  choice — a crash costs at most the commit, never a corpus of zeros.
- **A commit interrupted between its rename and its cleanup opens the newest generation only.** The
  successor is the flush of everything live at commit time, so the replaced file is a subset of it;
  chaining both would return every shared document twice and bring back the ones the commit deleted.
  A directory left with a temporary segment and no readable generation is **refused**, because opening
  it as a new store would report an empty corpus instead of a lost one — unless the journal holds
  entries, in which case those entries are the corpus and the store replays them. The mapped-mode commit of that
  path is **unverified on Windows**: Windows refuses to delete a file while a mapping is open, and
  every CI job here runs on Ubuntu. On Windows, dispose a mapped store before committing it.
- **Merging returns a new index rather than mutating this one.** A search in flight reads the segments,
  their bases and the length array, and a write between those reads would fold half the old chain and
  half the new one. Swapping the reference an engine holds is the pattern `AtomicEngineReference`
  publishes for exactly this.

The tokenizer must be the one the segments were written with: the dictionary holds normalized terms,
so a lookup is by the normalized form. Mutations are not safe to interleave with a search in flight —
apply them from one thread, the same contract `InMemoryTextIndex` documents.

### How a store opens its segments

A store opens the segments already in its directory in one of two modes, and the choice is a measured
trade rather than a matter of taste:

```csharp
using var store = new SegmentedTextIndex("./corpus", mode: SegmentSourceMode.Mapped);
```

| | `Array` (default) | `Mapped` |
|---|---|---|
| opening, 40 k / 200 k documents | 6425 / 49307 µs | 6644 / 46991 µs |
| search | 1.00× | **1.10-1.14×** |
| heap per open, 40 k / 200 k | 15.3 / 75.9 MB | 3.2 / 15.9 MB |

Opening is at parity — a mapped source does *not* open slower, which corrects an earlier figure taken
on a loaded host. What mapping saves is the segment's own bytes, about 300 a document, and that saving
is what matters once a segment is large enough that its copy into memory stops the store fitting. What
it costs is a multiplier on every query and a lifetime: a mapped store holds a view over a file and
must be disposed, after which it is unusable. An `Array` store holds nothing that needs releasing. The
multiplier above was measured on queries of about a millisecond; on the microsecond tail queries the
[store lane](../benchmarks.md#store-lane-store-what-the-deployable-path-costs-per-query) reads **2.0×**
for the same choice, so what mapping costs belongs with the query and not with the mode.

A segment stores the whole document — its id, its text, its metadata fields, its category and its text
fields — so a document reads back as the document that was written.

Each term's postings carry their **positions**, so a phrase or proximity query reads them instead of
re-tokenizing the document: `GetTermPositions` seeks the entry and decodes it. The positions are the
third plane of the posting block, and they cost about 15% of a segment on the measured corpus — against
a segment that is already 72% document text. A segment written before this format is refused by the
reader rather than misread.

### The write-ahead journal

A store opened and written to keeps **one file of what changed** rather than a corpus per write. Opening
it replays those entries over the newest segment, so a process that ends without a commit costs at most
the tail the flush policy has not forced to the device. Opening is a **read**: a corpus replica on a
read-only mount opens, replays and answers queries, and the first write is what asks the directory for
write access.

```csharp
using var store = new SegmentedTextIndex("./corpus");          // journal on, flushed in groups
using var durable = new SegmentedTextIndex("./corpus", journal: new JournalOptions
{
    Flush = JournalFlushPolicy.PerWrite,                       // or OnCommit, or Grouped (default)
    MaxRecords = 100,                                          // grouped: entries per flush
    MaxDelay = TimeSpan.FromMilliseconds(50),                  // grouped: time before a flush
});
```

Three policies, and what a power loss reaches back through:

| policy | what a crash costs | what it costs to write |
|---|---|---|
| `Grouped` (default) | the last `MaxRecords` entries, or `MaxDelay` | one device flush per group |
| `PerWrite` | nothing already returned | a device flush per write |
| `OnCommit` | everything since the last commit | nothing beyond the write itself |

The grouped bound is checked on append and on dispose, so it bounds a **stream** of writes: a writer
that stops stops with its last flushed entry, and `FlushJournal()` closes the window at a cadence of
your own — after a request, before answering a client. `OnCommit` reaches the operating system as each
write happens, which survives a process crash but not a power loss.

Two consequences worth knowing. The segment records the last journal entry it contains, so a replay
stops at what the commit already holds rather than applying a write twice — re-indexing a document is
not idempotent, it tombstones the old ordinal and adds a new one. And a directory holds **one journal
and one writer**: `Commit` and `Merge` return the store that owns the journal, and the store they
replaced refuses further writes. A store opened with `JournalOptions.Disabled` writes no journal and
loses its overlay on a crash before the next commit — the behaviour of a corpus assembled as one batch —
and it **refuses to open** a directory whose journal holds entries it would otherwise have to ignore,
rather than serving a corpus that is missing them.

Each record frames itself with a length, a checksum of that length, its payload and a checksum of the
payload. The two checksums separate the two failures a journal meets. A **torn tail** — the record a
crash interrupted — cannot present a whole frame, so opening reports what it found in
`TruncatedJournalBytes` and the first write cuts the file back to a boundary before appending; the
report is there because the alternative is a store that opens, answers, and is quietly missing the tail
of its last write. A **damaged** record — length or payload that does not match its checksum — makes the
store refuse to open rather than replay an operation it cannot vouch for. The length has its own
checksum because a corrupted length is indistinguishable from an interrupted write by framing alone,
and reading it as a tear would drop every record after it in silence.

The journal grows until a commit truncates it, and opening replays what it holds at about the cost of
adding each document once (58 µs measured); a store that never commits pays that on every open, so the
commit still has a cadence to choose — the journal makes it a choice rather than the only durability
point.

**What an `Add` costs under each policy.** `StoreWriteBenchmarks` prices one `Add` for each policy over
500 documents cut from its own 20,000-document, 124-token, 100,000-term corpus, in a directory of its
own per invocation. The rows write on this host's ext4 root rather than under `/tmp`, which is a tmpfs
here and where a device flush is nearly free. Each row runs in its own process with `--job medium`, so
a row's iterations are not interleaved with another policy's:

| policy | pass 1, mean µs per `Add` | pass 2, mean µs per `Add` | pass 3, mean µs per `Add` |
|---|---:|---:|---:|
| `Disabled` | 81.0 | 39.3 | 43.0 |
| `OnCommit` | 70.9 | 49.4 | 52.6 |
| `Grouped` (default) | 120.5 | 91.7 | 91.4 |
| `PerWrite` | 3224 | 3672 | 3679 |

The three columns are the same command at 20:59, 21:04 and 21:13, on a host shared with other builds,
and they are all here because the disagreement is part of the method: `PerWrite`, which waits on the
device, spans 3.22-3.68 ms (12 %), while the rows that compute span 39-81 µs (`Disabled`, 2.1×),
49-71 µs (`OnCommit`) and 91-121 µs (`Grouped`, 1.3×). The two later passes agree within 10 % on every
row; the first is the outlier, reading 32-106 % higher on the three CPU-bound rows and 12 % lower on
`PerWrite`. What is stable across all three is the ordering and the gap *inside* a pass. `OnCommit` is
within about 10 µs of `Disabled` — the append and the page-cache write, at this row's resolution.
`Grouped` is 39-50 µs above `OnCommit` for five flushes over 500 documents. `PerWrite` is 3.15-3.63 ms
above `OnCommit`.

The journal's own measurement gives a 1 KiB entry at **0.5 µs** appended, **3.1 ms** flushed and
**33 µs** with one flush per hundred entries (3.1 ms / 94), from a journal-level harness without the
store around it. The store-level rows reproduce the flush and the grouping: the grouped run flushes
five times where `PerWrite` flushes five hundred times. They put the grouped cost above what that
figure says: 39-50 µs per `Add`, i.e. 3.9-5.0 ms per flushed group of a hundred records, against
3.15-3.63 ms for the single-record flush the same pass pays. In the two passes that agree, the group
is 3.9-4.2 ms against 3.6 ms. The group's flush carries a hundred records' text rather than one
record's, and these rows do not separate the flush syscall from the bytes it writes back. Read the
33 µs as the journal-level amortised flush and 39-50 µs as what an end-to-end `Add` pays for it.

```bash
dotnet build bench/LexiSharp.Benchmarks -c Release

for policy in Disabled Grouped OnCommit PerWrite; do
  dotnet bench/LexiSharp.Benchmarks/bin/Release/net10.0/LexiSharp.Benchmarks.dll \
    --filter "*AddWithJournal$policy*" --job medium
done
```

The rows write into `bin/Release/net10.0/journal-bench-*`, deleted by the benchmark's cleanup. Run
them from a checkout on a real filesystem: on this host `/tmp` is a tmpfs, and a flush row measured
there prices a device it never touched.

Not measured by these rows: a device power loss — they flush, and nothing cuts the power; Windows; any
filesystem other than this host's ext4/NVMe; and the replay at open, which the 58 µs above measures and
these rows do not. The 500-document count makes a `Grouped` run five flushes and a `PerWrite`
invocation under two seconds. A longer run would amortise the per-invocation directory and journal
creation over more documents; those are inside the invocation and the rows include them without
separating them.

### The store through the typed facade

`LexiSharpIndex<T>` can be a store instead of an in-memory index, so the same typed API gains
persistence without the caller touching `SegmentedTextIndex`:

```csharp
using var search = new LexiSharpIndex<Article>(o =>
{
    o.Id = a => a.Id;
    o.Text = a => a.Body;
    o.Directory = "./corpus";                 // a SegmentedTextIndex, not an InMemoryTextIndex
    o.Load = id => repository.Get(id);        // id → document, for a store reopened without re-adding
});

search.AddRange(articles);
search.Commit();                              // the pending writes become one segment on disk
```

`Commit()` and `Merge()` each replace the store with a new one and rebuild the search pipeline around
it, because a store returns a new instance rather than mutating the old. `Search` reads the current
pipeline, so it always sees the committed corpus; `Engine` and `TextIndex` report the current instances
too, and a caller that captured either before a commit must read it again. A
`SegmentSourceMode.Mapped` index holds views over its segment files and must be disposed.

Two combinations a store cannot serve, refused rather than silently degraded: an index-time
`TermExpander` (its expansion writes through an in-memory base index, so the terms would never reach
disk), and a field-weighted scorer (a store keeps no per-field statistics — its `TextFields` are
searchable by a plain query but `BM25F` over it would read absent statistics). A reopened store does
not hold the caller's documents, so `Load` maps an id back to a `TDocument`; it defaults to the index's
own document when `TDocument` is `SearchDocument`.


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
