# LexiSharp

[![CI](https://github.com/manuc66/lexisharp/actions/workflows/ci.yml/badge.svg)](https://github.com/manuc66/lexisharp/actions/workflows/ci.yml)
[![CodeQL](https://github.com/manuc66/lexisharp/actions/workflows/codeql.yml/badge.svg)](https://github.com/manuc66/lexisharp/actions/workflows/codeql.yml)
[![codecov](https://codecov.io/gh/manuc66/lexisharp/graph/badge.svg)](https://codecov.io/gh/manuc66/lexisharp)
[![SonarCloud](https://sonarcloud.io/api/project_badges/quality_gate?project=manuc66_lexisharp)](https://sonarcloud.io/summary/new_code?id=manuc66_lexisharp)
[![NuGet](https://img.shields.io/nuget/v/LexiSharp.svg)](https://www.nuget.org/packages/LexiSharp/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

> A composable information retrieval toolkit for .NET — build, measure and inspect search
> pipelines, from lexical BM25 to hybrid and reranked retrieval.

LexiSharp provides composable interfaces and implementations for indexing plain text and
retrieving, ranking and classifying documents: an in-memory inverted index, four ranking
strategies, rank fusion, reranking, and **model-agnostic seams** for dense, learned-sparse and
neural scoring — the models themselves stay in your application. It also ships what it takes to
judge a pipeline rather than guess at one: standard IR metrics, BM25 parameter tuning, a
benchmark CLI over your own corpus, per-term score explanations and an end-to-end `SearchTrace`.
The core package references **no NuGet package at all**.

## See it running

Five retrieval strategies over one corpus, compared live — plain BM25, corpus-derived
semantic expansion, dense hashing embeddings, reciprocal-rank fusion and a term-overlap
rerank — with per-lane latency, highlighting and a click-through "why did this rank here?"
panel. No model, no external service.

```bash
dotnet run --project samples/LexiSharp.Demo
# → http://localhost:5000
```

![The demo comparing five retrieval strategies over one corpus — BM25, PMI expansion, hashing embeddings, RRF fusion and a term-overlap rerank, with per-lane latency and highlighting](docs/images/demo.png)

The demo is the fastest way to *see* what the composable pieces buy you: the whole wiring is
`DemoSearchService` (five engines over one corpus) plus a single static `wwwroot/index.html`.
Clicking a hit also shows the **`SearchTrace` chain** for that document — the stages it passed
through and the score going in and out of each:

![The explain panel of the demo, with the ranking chain of one document in the rerank lane: two BM25 scores from the lexical and semantic lanes, the dense score, the RRF merge with its per-source scores, and the cross-encoder verdict, each with the score going in and coming out](docs/images/demo-explain.png)

```
score   4.6925 -> 4.6925  BM25
score   3.1642 -> 3.1642  BM25
score   0.4867 -> 0.4867  Dense
merge   0.0492 -> 0.0492  dense=0.4867 lexical=4.6925 semantic=3.1642
rerank  0.0492 -> 100     CrossEncoder(QueryTermOverlap)
```
Each lane states what actually backs it — the semantic lane uses `PmiTermExpander`, the dense
lane a `HashingEmbeddingProvider`, the rerank lane a local term-overlap `ICrossEncoderScorer`
— and all three are swappable for a real model behind their existing seam. See
[Reference demo](#reference-demo-sampleslexisharpdemo) for the details.

Want evidence rather than a demo? The same engines are scored on NFCorpus, SciFact and
ArguAna against BEIR's published BM25 numbers in
[the eval harness](bench/LexiSharp.Eval/README.md), which you can run yourself.

## Status & scope

### Evidence

- **Measured against published baselines.** `bench/LexiSharp.Eval` runs the engines over three
  public BEIR corpora (NFCorpus, SciFact, ArguAna), md5-verified on download, and reports
  nDCG@10/MAP@10/MRR@10/R@10 next to the published BM25 numbers: BM25 lands within 0.5 % of
  the reference on SciFact and 8 % below it on ArguAna, and the best stack — BM25 + dense,
  RRF-fused, cross-encoder reranked — reaches **0.346** on NFCorpus against BEIR's **0.325**.
  Reproduce it with `dotnet run --project bench/LexiSharp.Eval`; full tables, per-dataset
  numbers and the effect of the opt-in `--stem porter` are in
  [its README](bench/LexiSharp.Eval/README.md).
- **Zero runtime dependencies in the core.** The `LexiSharp` package references no NuGet
  package at all — inverted index, BM25, every scorer and every decorator are BCL only.
  `LexiSharp.MessagePack`, `LexiSharp.Postgres` and the eval harness each bring their own
  (MessagePack, Npgsql, ONNX Runtime).
- **Behavior is specified by tests.** The documented behavior below is covered by the xUnit
  suite; the Postgres/ParadeDB integration tests run against a live instance when
  `POSTGRES_TEST_CONNECTION` is set and self-skip otherwise (see *Building & testing*).
- **Combinatorial coverage.** Engines, scorers, rerankers and mergers are tested individually
  *and* in the combinations the docs describe. Stated plainly rather than hidden: not every
  pairing is exercised, so treat an unusual combination as supported but unproven until you
  test it on your data.
- **Tracing cost is only partly measured.** `SearchTrace`'s allocation is pinned by
  BenchmarkDotNet and is stable across runs (a saturated trace adds 0 bytes per search; a fresh
  trace per search +0.47 KB). Its **time** cost is **unmeasured**: on the machine used for the run
  the baseline benchmark itself varied 2.3× between identical runs and the traced variant once came
  out faster than the baseline, so no timing is quoted. Read it as unknown, not as free. Details and
  machine configuration in [BENCHMARKS.md](BENCHMARKS.md#ranking-trace-searchtrace--allocation-only-no-timing).

### Scope and limits

- **Version 0.4.0, single maintainer.** The library is young and its public API may still
  change between minor versions — pin a version and read the release notes if you adopt it
  early. Contributions and feedback are welcome.
- **Built on established IR.** The techniques implemented (BM25, RRF, SPLADE-style sparse
  retrieval, MaxSim) follow well-documented information-retrieval literature; the value here
  is a small, dependency-free .NET implementation of them, not new research.
- **Performance numbers are indicative.** They live in [BENCHMARKS.md](BENCHMARKS.md) and were
  measured on one machine — always measure on your own corpus.

## Features

- **BCL-only core**: the `LexiSharp` package pulls in no NuGet dependency at all — index,
  scorers, rerankers and decorators are all base class library. Reach for
  `LexiSharp.MessagePack` or `LexiSharp.Postgres` only when you want persistence or SQL.
- **Pluggable architecture**: an `ITextIndex`, `ITextScorer` and `ITokenizer` are
  independent contracts; algorithms can be swapped without touching the engine.
- **Drop-in entry point** (`LexiSharpIndex<T>`): a typed facade that maps your own document
  type to the engine and back — index objects, get your objects back — with fluent options for
  the scorer, tokenizer, fuzzy matching, synonyms and an optional reranker.
- **Document sources** (`LexiSharp.Sources`): loaders for the data on your disk — markdown
  with YAML front matter, plain text files and JSON arrays — producing id + text + fields +
  category records ready to index. `MarkdownLoadOptions.TextFieldNames` promotes named front-matter
  keys (typically `title`) to indexed text fields, so a document's title is searchable rather than
  only filterable.
- **Benchmark CLI** (`LexiSharp.Cli` + `LexiSharp.Benchmarking`): an in-core runner compares
  stock scorers, tuned BM25 and RRF hybrids on your own corpus with labeled queries, reporting
  the standard retrieval metrics and latency; a console front-end drives it from the command
  line. It also compares two configurations **query by query** (`diff`), and records or replays a
  **golden master** (`baseline` / `verify`) so a change to the scoring or merging path shows up as
  a reviewable diff — see the [reference corpus](bench/reference-corpus/README.md).
- **ASP.NET Core endpoint** (`LexiSharp.AspNetCore`): a minimal-API extension that maps any
  `LexiSharpIndex<T>` to a `GET /search` endpoint with pagination, minimum score and text
  highlighting over HTTP.
- **Four ranking strategies** behind the same `ITextSearchEngine`:
  - `Bm25Scorer` — Okapi BM25, with ready-made `Bm25Parameters` profiles (`Balanced`,
    `Aggressive`, `Conservative`),
  - `TfIdfScorer` — TF-IDF,
  - `QueryLikelihoodScorer` — probabilistic language model (Jelinek-Mercer smoothing),
  - `BooleanScorer` — exact AND/OR filter.
  - `Bm25PlusScorer` and `Bm25LScorer` — the published BM25+ (Lv & Zhai 2011) and BM25L
    (Lv et al. 2006) variants, each with a lower bound `delta` on the term frequency.
    **Measured: neither beats a tuned BM25 on any corpus tried here** — see *BM25 variants*.
- **BM25F** (`Bm25FScorer`): the same `ITextSearchEngine` contract over documents that declare
  `TextFields`, so a term in a title can be weighted above the same term in a body — per-field
  weights, per-field length normalization, and an explainable per-term breakdown. Requires an
  index that tracks fields. **Its retrieval quality is unmeasured**; see *BM25F*.
- **In-memory inverted index** (`InMemoryTextIndex`) with term positions, document
  frequencies, corpus statistics and incremental `Add`/`Remove`, plus an index statistics
  snapshot (`GetStatistics`: documents, vocabulary, tokens, average length, vocabulary richness).
- **Multi-field documents**: a document's `TextFields` are indexed as named fields, and the index
  answers per-field term frequency, field length, average field length and field document frequency
  (`FieldTermFrequency`, `FieldLength`, `AverageFieldLength`, `FieldDocumentFrequency`, plus
  `Fields` and `HasFieldStatistics`) — the groundwork a field-weighted scorer needs. The **flat**
  statistics stay the union of every field, so a plain BM25 query still finds a term that only
  occurs in a title, and a single-field document is unaffected. A quoted phrase matches inside one
  field and never bridges two. See *Multi-field documents*.
- **Score boosting** (`BoostedTextSearchEngine`): a decorator that applies **signed** score
  adjustments (multiplicative factor and/or additive offset) per result — boost a category or a
  priority, damp or penalize stale matches — without touching the underlying engine.
- **Second-stage reranking**: an `IReranker` seam and the `RerankedTextSearchEngine`
  decorator (over-fetch, re-rank, guard rails) in the core; shipped rerankers include a
  diversity-preserving **MMR**, a **cascade** pipeline that chains any number of
  reranking stages with per-stage trimming, a **cross-encoder** reranker driven by a
  consumer-provided pairwise scoring model (`ICrossEncoderScorer`), and a **ColBERT MaxSim**
  reranker that re-scores a shortlist token-by-token with late interaction
  (`ITokenEmbeddingProvider`), and a **proximity** reranker (`ProximityReranker`) that reads the
  index's term positions to re-order by how tightly the query terms cluster, in either a **damp**
  or a **boost** shape. **Measured: proximity does not improve retrieval on any corpus tried
  here** — see *Proximity*.
- **Sparse learned embeddings**: `SparseTextSearchEngine` and its `ISparseEmbeddingProvider`
  seam bring SPLADE/uniCOIL-style retrieval (.NET-core only, weights learned, inverted-index
  scoring kept) without pulling ONNX into the library — the model lives in the consumer.
- **In-memory dense retrieval**: `InMemoryVectorSearchEngine` ranks by cosine over an
  `IEmbeddingProvider`'s vectors (with `Export`/`Import` to reload without re-embedding), the
  in-process counterpart of the PostgreSQL `pgvector` engine — and `HashingEmbeddingProvider`
  makes the whole embedding stack usable and testable with **no model and no dependency**.
- **Semantic lexical expansion** (`LexiSharp.Expansion`): an `ITermExpander` seam that widens
  a query — or any document, at index time — with corpus-derived related terms
  (`PmiTermExpander` learns PPMI/co-occurrence associations from your own documents), applied
  either to documents (`ExpansionTextIndex`) or to the query (`ExpandingTextSearchEngine`),
  zero-new-dependencies, pure .NET core; the seam is where a real neural SPLADE model plugs in later.
- **Reference demo app** (`samples/LexiSharp.Demo`): an ASP.NET Core page that compares BM25,
  semantic expansion, dense hashing embeddings, hybrid RRF and a term-overlap rerank side by
  side on one corpus, with latency, highlighting and a click-through "why did this rank here?"
  explanation — no model, no external service.
- **Metadata filters**: declarative, AND-composed filters over document fields
  (`MetadataFilterOperator`: equal, not-equal, contains, numeric-or-ordinal greater/less than)
  in `SearchOptions` — honored by every backend (stock in-memory and SQL) before scoring.
- **Pagination**: `SearchOptions.Offset` cuts any window `[Offset, Offset + Limit)` of the
  ranking — honored by the stock engine, the boost/rerank decorators, the hybrid merger
  (the page comes from the merged ordering) and every SQL backend.
- **Phrase queries**: double-quoted segments (`"machine learning"`) must appear at
  consecutive document positions (several phrases are AND-ed), while the free terms around
  them keep scoring — free terms never hard-filter a mixed query. Natively honored by the
  stock engine (index positions), PostgreSQL (`websearch_to_tsquery`) and ParadeDB (`###`).
- **Highlighting** (`LexiSharp.Highlighting`): `TextHighlighter` wraps query matches in the
  original text (`HighlightFull`) or returns padded, word-snapped snippets (`Highlight`) —
  driven by the tokenizer's span mode (`TokenizeWithSpans`: term + `[Start, Length)` offsets
  into the source).
- **Prefix &amp; fuzzy queries**: `neural*` expands to every indexed term with that prefix,
  `catt~`/`catt~N` fuzzy-matches within N edits (default 1, clamped to 0–2) — search-time
  vocabulary expansion on the stock engine (`IVocabularyIndex`), 64 terms max per atom.
  A **selective** mode (`FuzzyOnlyOutOfVocabulary`, or `SearchOptions` of the same name)
  keeps a term already in the vocabulary exact, so only genuinely unknown words are
  corrected and correctly spelled tokens can no longer be degraded by close variants.
- **Synonyms** (`SynonymMap`): one-way rewrites and bidirectional equivalence groups,
  tokenized at engine construction and applied to free query terms — one level deep
  (non-transitive), never inside quoted phrases.
- **Facets** (`IFacetedSearchEngine`): `SearchWithFacets` returns the ranked page plus value
  counts per requested `Fields` entry over the whole match set — independent of
  `Offset`/`Limit`, ordered by count then value.
- **Span-first API**: the text entry points — `ITokenizer.Tokenize`/`TokenizeWithSpans`,
  `QueryParser.Parse`/`SplitRaw`, `ITextSearchEngine.Search`,
  `IFacetedSearchEngine.SearchWithFacets` and `RankedTextSearchEngine.Explain` — each have a
  `ReadOnlySpan<char>` overload that avoids materializing the query as a string; default
  interface implementations forward to the string path so existing implementers keep working.
- **Cost-based routing** (`RoutedSearchEngine`): opt-in decorator over several pre-filled
  engines that forwards each query to the cheapest one, chosen by an `IQueryCostEstimator` —
  the default `CheapestByCandidateCountEstimator` uses per-engine `IQueryCostProbe` estimates.
- **Intent-based routing** (`RoutingSearchEngine`): opt-in decorator over pre-composed routes
  (engine + optional metadata filters) that asks an `IQueryRouter` you supply — a rule, a
  classifier or a model — which route to run, with a confidence threshold and a fallback. The
  route's filters are AND-ed onto the caller's. The seam is model-agnostic: LexiSharp never
  runs a model itself.
- **Lexical similarity** (`LexiSharp.Similarity`): pairwise token-set measures (Jaccard,
  Sørensen–Dice) over the library tokenizer, a `pg_trgm`-style character trigram similarity,
  and a rolling Levenshtein edit distance — near-duplicate detection and fuzzy matching with
  no index.
- **Keyword extraction** (`LexiSharp.Keywords`): a corpus-backed **TF-IDF** extractor
  (demotes corpus-frequent words) and a graph-based **TextRank** extractor (weighted
  co-occurrence graph + PageRank), both deterministic and tokenizer-configurable.
- **Explainable scoring**: `Bm25Scorer`, `TfIdfScorer`, `QueryLikelihoodScorer` and
  `BooleanScorer` implement `IScoreExplainer`, and `RankedTextSearchEngine.Explain` returns a
  per-term breakdown (TF, IDF, term score, length normalization, parameter values) of any
  ranking decision.
- **Ranking trace** (`SearchTrace`): opt-in, `SearchOptions.Trace` records every stage a document
  passed through — score, merge (per-source scores), rerank (before/after), boost (factor/offset)
  and the route a query took — so a final rank is walkable end to end. A null trace is inert and
  a trace is bounded by the page, never the corpus.
- **Source agreement** (`RetrievalAgreementAnalyzer`): classifies each document of a fused page as
  `Unanimous`, `Disputed`, `Lukewarm` or `SingleSource` from the per-source scores, normalized per
  source so the incomparable scales (BM25 ~4.8, cosine ~0.48) can be read together. It says
  whether a document is on the page because everything agreed, or because one retriever insisted.
- **Calibrated confidence** (`ScoreConfidence`): maps a result set's raw scores — BM25 output
  and friends, whose scale is not a probability — to a per-result confidence in [0,1],
  either from the winner margin (gap to the next result, scale-invariant) or from a logistic
  z-score against the set's own distribution. `TopConfidence` feeds a `minConfidence` gate
  that raw lexical scores cannot.
- **Evaluation metrics** (`RetrievalMetrics`): `Precision@k`, `Recall@k`, `F1@k`, binary and
  **graded** `nDCG@k` (exponential gains), plus `ReciprocalRank@k` (→ MRR) and
  `AveragePrecision@k` (→ MAP).
- **BM25 tuning**: `Bm25ParameterTuner` grid-searches `k1`/`b` against your own validation
  queries, judged by `Precision@k`, `Recall@k`, `F1@k` or `nDCG@k`. `Bm25FParameterTuner` does the
  same for BM25F's `k1`/`b` and per-field weights, and reports `WeightingHelped` — the answer to
  "does weighting a field help on my corpus", which on every corpus tried here is no.
- **Supervised classification** (`NaiveBayesClassifier`): multinomial Naive Bayes with
  Laplace smoothing, exposing a dedicated `ITextClassifier` interface.
- **Configurable tokenizer**: Unicode NFKD normalization and diacritics removal,
  lowercasing, optional stop-word removal, optional n-grams, and a pluggable
  `IStemmer` seam — with `PorterStemmer` (English, no dependency, opt-in) shipped in the core.
  Tokenization is SIMD-accelerated (`SearchValues` + `IndexOfAnyExcept`, with a
  `System.Text.Ascii` fast path in normalization) — measured at ~5× faster on ASCII-only
  input than on input requiring accent removal ([BENCHMARKS.md](BENCHMARKS.md)).
- **Optional backends**, shipped as separate packages:
  - `LexiSharp.Postgres` — PostgreSQL backends implementing the same `ITextSearchEngine`:
    a lexical engine over `tsvector` + GIN + `unaccent`, an ANN engine over `pgvector`
    (HNSW/IVFFlat) driven by an external `IEmbeddingProvider`, a learned-**sparse** engine
    over `pgvector sparsevec` (HNSW) driven by an `ISparseEmbeddingProvider`, an approximate
    **fuzzy** engine over the `pg_trgm` trigram extension (with optional `fuzzystrmatch`
    refinement), and a **true Okapi BM25** engine over the ParadeDB `pg_search` Tantivy
    extension (`ParadeDBTextSearchEngine`, AGPL-3). The extension engines share one documents
    table and are picked at instantiation, exactly like the other backends.

## Quick start

```csharp
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;

ITextSearchEngine engine = new RankedTextSearchEngine(
    new InMemoryTextIndex(),
    new Bm25Scorer());

engine.Index(new[]
{
    new SearchDocument("1", "The search engine uses BM25 to rank the results"),
    new SearchDocument("2", "TF-IDF is a classic method of textual search"),
    new SearchDocument("3", "Italian cuisine is renowned in Rome"),
});

IReadOnlyList<SearchResult> results = engine.Search("textual search");

foreach (var result in results)
    Console.WriteLine($"{result.DocumentId} - {result.Score:0.###}: {result.Document.Text}");
```

### Drop-in index (`LexiSharpIndex<T>`)

The fastest way in: a typed facade that maps your own documents to the engine and back, so
you go from "a list of objects" to "working search" in a few lines. `TDocument` can be
`string` or `SearchDocument` (selectors default to the obvious mapping), or any class with
explicit id/text selectors:

```csharp
using LexiSharp;

var search = new LexiSharpIndex<MyDocument>(o =>
{
    o.Id = d => d.Id;
    o.Text = d => d.Body;
    o.EnableFuzzy = true;        // plain terms behave like `term~1`
});

search.Add(documents);

var hits = search.Search("architecture distributed systems");
foreach (var hit in hits)
    Console.WriteLine($"{hit.DocumentId} - {hit.Score:0.###}: {hit.Document.Body}");
```

`Search` returns typed hits carrying the original document; `SearchWithFacets` adds facet
buckets over the match set, `Explain` returns the per-term score breakdown, `Statistics` a
corpus snapshot. `Highlight: true` on the query options wraps the matched terms of each hit.
The scorer/tokenizer knobs of the rest of the library stay reachable through
`LexiSharpIndexOptions<T>` (`UseBm25`, `UseTfIdf`, `UseQueryLikelihood`, `UseBoolean`,
`RemoveStopWords`, `Stemmer`, `NGramMax`, `Synonyms`, `Reranker`, ...).

### Document sources (`LexiSharp.Sources`)

Pair the facade with the loaders to index data that lives on disk: each loader produces a
`LoadedDocument` (id + text + optional fields/category) that maps straight into a
`LexiSharpIndex<LoadedDocument>` or a `SearchDocument`.

```csharp
using LexiSharp.Sources;

var notes = MarkdownLoader.LoadDirectory(@"./notes");          // *.md, *.markdown, *.mdx
var ledgers = TextFileLoader.ScanDirectory(@"./ledgers");      // any plain text files
var records = JsonDocumentsLoader.Parse(json, new JsonDocumentLoadOptions
{
    IdProperty = "docid",
    TextProperty = "content",
    CategoryProperty = "bucket",
});
```

- **`MarkdownLoader`** — reads a file (`LoadFile`) or a whole directory (`LoadDirectory`,
  recursive, hidden paths skipped), parses the optional `---` YAML front matter into fields
  (`title`, `category`, `tags` as `[a, b]` lists, plus any other `key: value`), and indexes the
  remaining text. Document ids default to the full path for files and the forward-slash
  relative path for directory scans.
- **`TextFileLoader`** — whole file content is the indexed text; `title` and `source` fields
  are added automatically.
- **`JsonDocumentsLoader`** — parses a JSON array of objects; scalars become fields, scalar
  arrays are flattened to a comma-separated string; the id/text/category property names are
  configurable.

### Benchmark CLI (`LexiSharp.Cli`)

Compare ranking strategies over your own corpus without writing code. The runner
(`LexiSharp.Benchmarking` in the core) builds one shared in-memory index, evaluates every
selected configuration against the same labeled queries and reports nDCG/MAP/MRR/Recall/
Precision/F1 at the retrieval depth plus the per-query latency:

```
dotnet run --project bench/LexiSharp.Cli -c Release -- benchmark ./notes \
    --queries queries.json --qrels qrels.tsv --top-k 10
```

- `--queries` accepts a JSON object `{ "id": "query text", ... }` or a TSV `id⇥text`.
- `--qrels` is a TSV `qid⇥docid[⇥grade]` (grades are read as binary relevance); a query
  without any judgment is loaded but excluded from the metric averages.
- `--configs` selects the comparison: `bm25`, `bm25-tuned` (fits `(k1, b)` on the labeled
  queries), `tfidf`, `ql` (query likelihood), `hybrid` (RRF over BM25 + TF-IDF).
- `--json <path>` writes the results as a machine-readable report.

The same comparison is available in-process through `CorpusBenchmark.Run` over any
`IReadOnlyCollection<SearchDocument>` and `BenchmarkQuery` set, with custom engines reachable
through the public `BenchmarkConfig` constructor.

### Comparing two configurations, query by query

A pair of means cannot tell you what a change actually did. Two configurations can land 0.015 apart
while one of them rescued a query and lost two others — and the mean is silent about both.

```bash
dotnet run --project bench/LexiSharp.Cli -c Release -- diff ./notes \
    --queries queries.json --qrels qrels.tsv \
    --baseline bm25 --candidate bm25-semantic --top-k 5
```

```
mean nDCG@5: 0,8359 -> 0,8209 (-0,0150 per query)
queries: 1 improved, 2 degraded, 19 unchanged (net -1)

DEGRADED by BM25 + semantic
  long-document-02    0,500 -> 0,000 (-0,500)  lost: the judged document at rank 3 is gone
    "how often should a token be exchanged"
  paraphrase-02       0,579 -> 0,459 (-0,120)  demoted: rank 2 -> 3
    "how long is an access token valid"

IMPROVED by BM25 + semantic
  multilingual-01     0,710 -> 1,000 (+0,290)  same rank 1, score moved on the rest of the page
    "certificate rotation"
```

The `Reading` on each line names the cause rather than restating the numbers, because *lost*,
*rescued*, *demoted* and *same rank* are four different bugs. A net of zero is not "no change": it
is movements that cancelled out, which is precisely what a mean hides.

`CorpusBenchmark.Run` returns the same breakdown in `BenchmarkConfigResult.PerQuery` (metrics per
query, the retrieved ids, and the 1-based rank of the first judged document), and
`BenchmarkComparer.Compare(baseline, candidate, epsilon)` builds the comparison. It refuses runs over
different query sets or in different orders rather than reporting a comparison against nothing.
`--epsilon` (default `1e-9`) is the score difference below which a query counts as unchanged; the
reported mean delta stays raw arithmetic.

The [reference corpus](bench/reference-corpus/README.md) is the corpus the command above was run
against.

### ASP.NET Core search endpoint (`LexiSharp.AspNetCore`)

A minimal-API endpoint that exposes any registered `LexiSharpIndex<T>` over HTTP — a thin
package built on the core (no web framework of its own; you reference it from your ASP.NET
Core app):

```csharp
using LexiSharp;
using LexiSharp.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

var index = new LexiSharpIndex<SearchDocument>();
index.AddRange(loader.LoadDirectory("data"));
builder.Services.AddSingleton(index); // resolve the same<T> the endpoint uses

var app = builder.Build();
app.MapLexiSharpSearch<SearchDocument>(); // GET /search?q=...&limit=10&offset=0&highlight=true

app.Run();
```

- Query parameters: `q` (required), `limit` (default 10), `offset` (default 0),
  `minimumScore` (default −∞), `highlight` (default false).
- A blank `q` returns `400` with a `{ "field": "q", "message": ... }` error payload.
- With `highlight=true` each hit carries `highlightedText`, the document text with matched
  terms wrapped in `<em>`; the page honors `Limit`/`MinimumScore`/`Offset` exactly like the
  in-process `LexiSharpIndex<T>.Search`.

Change the ranking algorithm without rebuilding the index:

```csharp
var bm25Engine   = new RankedTextSearchEngine(index, new Bm25Scorer());
var tfIdfEngine  = new RankedTextSearchEngine(index, new TfIdfScorer());
var booleanEngine = new RankedTextSearchEngine(index, new BooleanScorer(BooleanMatch.AllTerms));
```

### Semantic lexical expansion (`LexiSharp.Expansion`)

A query (or a document) rarely uses the *exact* vocabulary of what it is about. The
`ITermExpander` seam lets the index enrich itself: each document is indexed as usual, then
gains a handful of **expansion terms** — related words learned from the corpus — stored at
synthetic positions after its literal tokens (a phrase query can never bridge the boundary).
`PmiTermExpander` learns those associations statistically from your own documents (windowed
co-occurrence, PMI-filtered, density-capped so function words don't leak); plug your own
implementation (e.g. a neural SPLADE model) behind the same interface:

```csharp
using LexiSharp;
using LexiSharp.Expansion;

// Learn the associations from the documents you are about to index (say, loaders output).
var expansion = PmiTermExpander.LearnFrom(corpus, options: new PmiTermExpanderOptions
{
    ContextWindowSize = 8,     // ±7 neighbours; 8 is the standard (LSA/PMI) default
    MaxWindowDensity = 0.5,    // drop words present in > half the windows (function words)
    MaxTotalTerms = 8,         // expansion budget per document
});

var index = new LexiSharpIndex<SearchDocument>(options => options.TermExpander = expansion);
index.AddRange(documents); // every document is expanded lazily at its own Add

var hits = index.Search("refresh"); // also finds "token", "expiry", "session" documents
```

The rediscovered terms enter the very same inverted index — BM25, phrase, highlighting and
every engine work on the enriched vocabulary untouched. The `bm25-semantic` preset of the
benchmark CLI measures the impact against plain BM25 on your own corpus.

The same expander can widen the **query** instead of the documents, with no reindexing, through
the `ExpandingTextSearchEngine` decorator (original terms stay first; syntax-bearing queries are
passed through untouched):

```csharp
using LexiSharp.Expansion;

ITextSearchEngine engine = new ExpandingTextSearchEngine(bm25Engine, expansion);
var hits = engine.Search("refresh"); // query becomes "refresh token access oauth session"
```

### Reference demo (`samples/LexiSharp.Demo`)

A self-contained ASP.NET Core app that runs **five retrieval strategies over the same corpus**
and compares them live — plain BM25, corpus-derived semantic expansion, dense hashing
embeddings, reciprocal-rank fusion, and a term-overlap rerank — with per-lane latency and
highlighting. Clicking any hit opens a **"why did this rank here?"** panel powered by
`LexiSharpIndex.Explain` (per-term contributions, IDF, length) or, for the federated/dense
lanes, the per-source scores from `HybridTextSearchEngine.SearchWithDetails`. No external model
or dependency: the semantic lane uses `PmiTermExpander`, the dense lane a
`HashingEmbeddingProvider`, and the rerank lane a local term-overlap `ICrossEncoderScorer` —
swap any of them behind its seam for a real model.

```bash
dotnet run --project samples/LexiSharp.Demo
# → http://localhost:5000  (search box + five comparison columns)
```

See [See it running](#see-it-running) for what this buys you and how to read the lanes.

### Classification

```csharp
using LexiSharp.Classification;

var classifier = new NaiveBayesClassifier();
classifier.Train(trainingDocuments); // requires a non-null SearchDocument.Category

foreach (var prediction in classifier.Predict("i cannot connect to the internet"))
    Console.WriteLine($"{prediction.Category}: {prediction.Probability:P}");
```

The classifier is a `IWeightedPredictor` too (`classifier is IWeightedPredictor`): a
spell-corrected token can carry less evidence than an exact match by passing
`WeightedToken`s directly. `Predict`/`PredictBest` accept a set of `excludedCategories` to
hide hot categories at runtime without retraining (probabilities renormalize over the rest).
`NaiveBayesOptions` tunes the scoring: a softmax `Temperature` (sharpening/flattening), an
`IdfMode` (`None` / `DocumentCount` = `log(1 + N/df)` / `ClassCount` = `max(0, log(C/df))`),
an `Alpha` smoothing coefficient (optionally applied to the priors through `SmoothPriors`) and
`SkipOutOfVocabularyTokens` (ignore unknown query terms instead of a Laplace penalty). Setting
`Complement` switches to **Complement Naive Bayes** (Rennie et al. 2003, matching scikit-learn's
`ComplementNB`): each class is learned from the complement of its documents and a query is
attributed to the class whose exclusion explains it least — a cheap robustness win when the
training labels are heavily imbalanced. `Train` must not overlap any `Predict`; concurrent
`Predict` calls are safe.

### Tokenizer customization

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
| ArguAna (1406 queries)              | 0.289 | 0.279 | 0.315 |

It helps on two corpora and **hurts on the third**, so it stays opt-in: ArguAna's whole-argument
queries are nearly all content words, the case where over-stemming has most to lose. Full tables
for every config are in
[the eval harness README](bench/LexiSharp.Eval/README.md); reproduce with
`dotnet run --project bench/LexiSharp.Eval -- --stem porter`. For another language, implement
`IStemmer` (or take Snowball) and pass it the same way.

### Score boosting (`BoostedTextSearchEngine`)

Wrap any engine to boost or damp its ranking without changing the engine. The boost is a
function of the whole result, so it can read the score, the document metadata or external
data (a closure over your own store):

```csharp
ITextSearchEngine boosted = new BoostedTextSearchEngine(baseEngine,
    result =>
    {
        double factor = 1.0;
        if (result.Document.Category == "priority")
            factor = 2.0;                                  // up-weighted metadata
        if (result.Document.Fields.TryGetValue("stale", out _))
            factor *= 0.5;                                 // damp old matches

        return new ScoreBoost(Multiply: factor, Add: -0.5); // factor and/or offset, signed
    });
```

Writes are forwarded to the inner engine; `Search` applies `score * Multiply + Add` to every
candidate, then re-sorts and re-applies `Limit`/`MinimumScore`. A plain double is accepted as
a multiplicative factor (`result => 2.0`). Positive boosts (factor &gt; 1, positive offset) and
negative ones (factor in (0, 1) damp, negative offset penalty) are equally expressible; factor 0
drops the document entirely. The decorator requests more candidates than the final limit
(`maxCandidates`, default 50) so boosted documents can surface.

### Reranking (`IReranker`, MMR, cascade)

Retrieve with recall, then re-rank a shortlist with precision. The core seam is `IReranker`;
`RerankedTextSearchEngine` decorates any engine (over-fetches, re-ranks, applies
`MinimumScore`/`Limit` on the final scores):

```csharp
using LexiSharp.Core;

IReranker reranker = ...;                                    // yours, or the MMR one below
ITextSearchEngine engine = new RerankedTextSearchEngine(baseEngine, reranker, maxCandidates: 100);
```

`LexiSharp` ships two built-in rerankers. **MMR** (Maximal Marginal Relevance)
re-orders candidates so each next pick is relevant *and* different from the picks before it —
near-duplicate results are pushed back; candidates without a vector are never penalized:

```csharp
using LexiSharp.Hybrid;

var vectors = new Dictionary<string, ReadOnlyMemory<float>>
{
    ["doc-1"] = embedding1, // pre-computed with your IEmbeddingProvider
    ["doc-2"] = embedding2,
};

IReranker mmr = new MaximalMarginalRelevanceReranker(vectors, lambda: 0.7, limit: 5);
```

**Cascade** chains any number of stages, trimming between stages so only the strongest
candidates reach the expensive final ones; it is itself an `IReranker`, so cascades nest:

```csharp
var pipeline = new CascadeRerankPipeline(
    new IReranker[] { lexicalReranker, mmr },
    new CascadeRerankOptions(StageLimit: 20, FinalLimit: 5, MinimumScore: 0.01));
```

**Cross-encoder** re-scores the shortlist with a pairwise model — a precision stage for cases
where whole-corpus scoring would be too expensive (ColBERT-style late interaction, an LLM
judge, ...). The model itself is a consumer-provided seam (`ICrossEncoderScorer`, same
contract as `IEmbeddingProvider`: LexiSharp never runs the model):

```csharp
IReranker cross = new CrossEncoderReranker(myOnnxCrossEncoder, limit: 5);
```

`CrossEncoderReranker` replaces each candidate's score with the model's, drops `0`/NaN/infinity
scores, and applies an optional `MinimumScore` and `Limit`.

**MaxSim** (ColBERT-style late interaction) re-scores the shortlist token-by-token instead of
as a single embedding: every token of the query is embedded, each scores against the whole
candidate's token embeddings (`max similarity per query token`, summed), so a query token never
has to "average itself away" across the document:

```csharp
using LexiSharp.Core;
using LexiSharp.Hybrid;

var tokenVectors = new Dictionary<string, IReadOnlyList<ReadOnlyMemory<float>>>
{
    ["doc-1"] = doc1TokenEmbeddings, // token embeddings pre-computed at index time
    ["doc-2"] = doc2TokenEmbeddings,
};

IReranker maxsim = new MaxSimReranker(
    myTokenEmbedder,                 // ITokenEmbeddingProvider (core seam, consumer-provided)
    tokenVectors,                    // doc-side token embeddings, pre-computed with the Passage role
    limit: 5,
    minimumScore: 0.0);
```

`MaxSimReranker` scores each candidate as `Σₜ max_tok cosine(q_t, d_tok)` — for each query token,
the best cosine against any of the candidate's token embeddings (the query side is re-embedded
with the `Query` role per search) — drops `0`/NaN/infinity scores
and ranks by total. It is the middle ground between whole-document cosine and the full
pairwise pass of a cross-encoder.

### Metadata filters

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

### Phrase queries

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

### Highlighting

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

### Prefix &amp; fuzzy queries

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

### Synonyms

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

### Facets

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

### Span-first API

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

### Cost-based routing (`RoutedSearchEngine`)

When the same corpus is reachable through several engines — say a stock in-memory engine and a
SQL backend — route each query to the one that will do the least work:

```csharp
using LexiSharp.Core;

var router = new RoutedSearchEngine(new[]
{
    new RoutedEngine("memory", memoryEngine),   // implements IQueryCostProbe
    new RoutedEngine("postgres", pgEngine),     // no probe: last resort
});

IReadOnlyList<SearchResult> hits = router.Search("machine learning");
```

The router owns no index — it never writes, and its `Index`/`Add`/`Remove`/`Clear` throw
`NotSupportedException`; populate the engines yourself. Each query runs on exactly one engine,
so the scores are that engine's own (the router never mixes or renormalizes them across
engines). The default `CheapestByCandidateCountEstimator` asks every engine implementing
`IQueryCostProbe` for `EstimateCandidateCount` and picks the smallest — ties keep the earliest
engine — while engines without the probe are only used when no costed engine exists. The stock
engine's estimate is the sum of the literal query terms' document frequencies; pass a custom
`IQueryCostEstimator` to route on engine priority, latency history or query shape instead.

The router is capability-preserving: `SearchWithFacets`, `SearchWithDetails` and `Explain` run
on the engine the estimator selects for that query, and the router's own
`EstimateCandidateCount` reports the smallest estimate across its engines (so a routed engine can
itself be a candidate inside another router). Because the selection is per query, a capability
the selected engine lacks throws `NotSupportedException` rather than silently re-routing.
`BoostedTextSearchEngine`/`RerankedTextSearchEngine` also forward `IQueryCostProbe` to their
inner engine (they do not change how many candidates a query touches); their score-mutating
wrapper does not currently surface facets/detailed/explain, whose contracts it cannot
preserve.

### Intent-based routing (`RoutingSearchEngine`)

Cost routing picks the cheapest engine; intent routing picks the *right* one. Route each query to
a pre-composed target — an engine plus optional metadata filters — chosen by a decision you
supply (a rule, a small classifier, or a model behind `IQueryRouter`):

```csharp
using LexiSharp.Core;

var router = new RoutingSearchEngine(
    new KeywordVsQuestionRouter(),                 // your IQueryRouter
    new[]
    {
        new SearchRoute("keywords", memoryEngine),
        new SearchRoute("questions", pgEngine, new[]
        {
            new MetadataFilter("kind", MetadataFilterOperator.Equal, "faq"),
        }),
    },
    fallbackId: "keywords",
    minimumConfidence: 0.6);

IReadOnlyList<SearchResult> hits = router.Search("how do I reset my password?");
```

The router only chooses among the ids it is given (`RouteAsync(query, candidateIds)`); it never
builds filters or touches indexes. The selected route's filters are AND-ed onto the caller's
`SearchOptions.Filters`. A `null` decision, an unknown id, a confidence below the threshold or a
thrown exception all run the fallback route — a broken router never breaks a search. The seam is
async (`ValueTask<QueryRoute?>`) because a model-backed router is naturally async, but the
synchronous `Search` blocks on it, like the PostgreSQL engines block on `IEmbeddingProvider`.
LexiSharp never runs a model itself: you provide the rule, classifier or model.

### Lexical similarity and keyword extraction

Pairwise similarity for near-duplicate detection and record de-duplication — token-set
measures over the library tokenizer, `pg_trgm`-style trigrams, and Levenshtein:

```csharp
using LexiSharp.Similarity;

bool duplicate = LexicalSimilarity.Jaccard(stored, incoming) > 0.5;
double fuzzy   = LexicalSimilarity.Trigram("kubernetes cluster", "kubernetes clusters");
int edits      = LevenshteinDistance.Distance("kitten", "sitting"); // 3
```

Keyword extraction pulls the representative terms out of a text. TF-IDF becomes corpus-aware
when built over an `ITextIndex`; TextRank needs no corpus at all:

```csharp
using LexiSharp.Keywords;

IKeywordExtractor tags = new TfIdfKeywordExtractor(someIndex, StopWordTokenizer);
IKeywordExtractor graph = new TextRankKeywordExtractor(StopWordTokenizer); // co-occurrence + PageRank

foreach (var keyword in graph.Extract(document.Text, topN: 5))
    Console.WriteLine($"{keyword.Term}: {keyword.Score:F3}");
```

### Index persistence (`LexiSharp.MessagePack`)

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

### Ranking trace (`SearchTrace`)

`Explain` breaks down one scorer's arithmetic. `SearchTrace` goes further: it records **every stage
a document passed through**, so a final rank can be walked back stage by stage — which engine
scored it, what each merger contributed, what the reranker changed, what the boost did, and which
route ran.

```csharp
using LexiSharp.Core;

var trace = new SearchTrace();
var hits = engine.Search("refresh token", new SearchOptions(Limit: 5, Trace: trace));

foreach (var step in trace.Steps)
    Console.WriteLine($"{step.Stage,-6} {step.DocumentId,-8} {step.Before,8:0.000} -> {step.After,8:0.000}  {step.Detail}");

// route   d1              0.800 ->            dense (fallback: below threshold or no opinion)
// score   d1      4.819997 ->      4.819997  BM25
// merge   d1      0.016393 ->      0.016393  lexical=4.819997 dense=0.812003
// rerank  d1      4.819997 ->      0.912000
// boost   d1      0.912000 ->      1.824000  x2 +0
```

`SearchOptions.Trace` defaults to `null`, and a null trace is inert: no engine records, allocates
or formats anything. Pass one and each stage appends to it as it runs, so a decorated pipeline
accumulates its whole chain.

**Cost — what is measured and what is not.** Measured with BenchmarkDotNet's `MemoryDiagnoser` and
stable across repeated runs: a saturated trace allocates **0 additional bytes** per search
(1.34 KB either way), and a trace created per search costs **~0.47 KB** (the object plus its
backing array).

A **time** figure is deliberately **not** given, and the reason is itself a measurement. On the
machine used for the run the *baseline* benchmark varied between 1.52 ms and 3.57 ms (2.3×) across
identical runs, BenchmarkDotNet reported bimodal distributions on the pre-existing search benchmarks
too, and the traced variant came out faster than the baseline in one run — which is impossible. A
delta under that spread is not measurable there, so quoting one would be fiction.

Full details, machine configuration and the reproduce command: [BENCHMARKS.md](BENCHMARKS.md#ranking-trace-searchtrace--allocation-only-no-timing).

**Bounds.** A trace stops recording past `Capacity` (default 256) and counts the overflow in
`Dropped`; check `IsTruncated` rather than assuming `Steps` is the whole story. Stage recording is
bounded by the page or the candidate shortlist, never by the corpus, so a trace cannot grow with
the index — this is what the `ScoreStageIsBoundedByThePageNotTheCorpus` test pins on a 20 000-doc
index.

**Not thread-safe.** A trace is a mutable collector: give each concurrent search its own, the same
way each gets its own `SearchOptions`.

### Source agreement (`RetrievalAgreementAnalyzer`)

A fused ranking hides *why* a document is on the page. `RetrievalAgreementAnalyzer` reads the
per-source scores in `DetailedSearchResult.Contributions` and classifies each document by how much
its sources agree:

```csharp
using LexiSharp.Core;

var page = hybrid.SearchWithDetails("refresh token", new SearchOptions(Limit: 20));
var reports = RetrievalAgreementAnalyzer.Analyze(page);

foreach (var report in reports)
    Console.WriteLine($"{report.DocumentId}  {report.Agreement}  strong: {string.Join(",", report.StrongSources)}");

var counts = RetrievalAgreementAnalyzer.Summarize(reports);
```

| Category | What it means |
|----------|---------------|
| `Unanimous` | several sources returned it, all found it convincing |
| `Disputed` | several returned it, they disagree — some convinced, some not |
| `Lukewarm` | several returned it, **none** convinced: it is on the page through a merger, not anyone's conviction |
| `SingleSource` | exactly one source returned it — the case worth surfacing, since a reader of the final score cannot tell |
| `None` | no source returned it; should not occur on a result page |

The categories are structural, not named, because source labels are yours: a setup calling them
`lexical`/`dense` and one calling them `bm25`/`cosine` describe the same shape, so a hard-coded
`LexicalOnly` would be wrong for half of them. `StrongSources` gives the names back.

**Normalization is not optional.** Raw contributions are not comparable across sources — LexiSharp's
dense lane returns a cosine in [0, 1] while its BM25 lane returns values around 1 to 10. Dividing
each contribution by the best score that source gave on the same page makes the values scale-free
without assuming anything about the scoring function. A source that returned nothing is reported as
`AbsentSources` rather than weak, because being outside a source's depth is a different fact from
being ranked low by it.

**The threshold is a heuristic.** `strongThreshold` (default `0.5`) is the share of a source's best
score at which a document counts as strongly supported. It is a round number chosen for
readability, **not a value validated against relevance data** — tune it against your own corpus, and
pass `sourceNames` so a source that silently contributed nothing still shows up as absent.

The demo is the quickest way to see the distinction matter. Running its six sample queries over its
26-document corpus, the fused page holds 9 `unanimous`, 8 `disputed`, 7 `singleSource` and
2 `lukewarm` — the obvious matches are unanimous, the arguable ones disputed, the lukewarm ones are
on the page through the merger with nobody enthusiastic, and some ride on a single lane. `None`
does not appear, and cannot: it classifies a document no source returned, so by definition it never
shows up on a result page.

### Multi-field documents

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

### BM25F (field-weighted BM25)

`Bm25FScorer` ranks a document that has *fields*. The term frequencies of the fields are summed
with a weight each, and the length normalization is taken over **the fields that contain the term**
— so a term living only in a short title is not penalized for the length of a long body it never
appears in. That is the real difference from running `Bm25Scorer` over the flattened text.

```csharp
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;

var index = new InMemoryTextIndex();
index.Index(new[]
{
    new SearchDocument(
        Id: "1",
        Text: "the body is long and rambles on at some considerable length about many things",
        TextFields: new Dictionary<string, string> { ["title"] = "ranking" }),
});

// A weight per field. A field left out keeps the neutral weight of 1, so an unset map means
// « treat every field equally », not « search the main text only ». A weight of 0 removes the
// field from the ranking entirely.
var engine = new RankedTextSearchEngine(
    index, new Bm25FScorer(fieldWeights: new Dictionary<string, double> { ["title"] = 2.0 }));

engine.Search("ranking");
```

Or from a preset, chaining weights:

```csharp
var scorer = new Bm25FScorer(Bm25FParameters.Balanced.WithWeight("title", 2.0));
```

`Bm25FScorer` implements `IScoreExplainer` (a per-term breakdown whose reported total equals the
score), `ITermOverlapScorer` (a document sharing no query term scores exactly `0`, so the engine
keeps its candidate fast path) and `IQueryPlannableScorer` (a planned search is bit-identical to an
unplanned one — both are asserted in the test suite).

**The formula** is BM25F as given in Robertson, Zaragoza & Taylor, *New formal models of BM25*
(2004), with two departures stated in the source so the behaviour is unambiguous: a single `b` for
all fields where the paper allows one `b_f` per field, and `k1` unrescaled by field count. The
`idf` is the document-level one, the same as `Bm25Scorer`'s.

**Retrieval quality, measured on all three BEIR corpora the harness supports** (nDCG@10; full
tables in [the eval harness README](bench/LexiSharp.Eval/README.md)):

| Config | NFCorpus | SciFact | ArguAna |
|---|---|---|---|
| BM25 (k1=1.5, b=0.75) | 0.308 | 0.662 | 0.289 |
| BM25 (tuned in-sample, oracle) | 0.311 | 0.664 | — |
| BM25F (unweighted) | 0.296 | 0.662 | **0.344** |
| BM25F (title 2.0) | 0.296 | **0.665** | **0.344** |
| BM25F (title 4.0) | 0.296 | 0.664 | 0.340 |
| BEIR's published BM25 | 0.325 | 0.665 | 0.315 |

ArguAna was run with `--no-tuned` (its grid search is the dominant cost); NFCorpus 323 judged
queries, SciFact 300, ArguAna 1406. BM25's own row reproduces the numbers this README already
quoted for the same harness, which is the cross-check that the title-field change below left plain
BM25 untouched.

**A correction the numbers forced, and it matters more than the table.** The rows above compare
*default* parameters against each other, and on the reference corpus tuning shows what that was worth:

```
Config                     nDCG@5      MAP@5      MRR@10        R@5
BM25                       0.8751     0.7841     0.9015     0.8409
BM25 (tuned)               0.8812     0.7917     0.9015     0.8409
BM25F                      0.8189     0.7386     0.8561     0.7955
BM25F (tuned)              0.8812     0.7917     0.9015     0.8409
```

**Tuned BM25F equals tuned BM25 to the digit on the reference corpus**, and the un-tuned gap was
BM25F's defaults (k1=1.2) being a worse fit for a 42-document corpus than BM25's (k1=1.5) — not a
per-field length term doing something useful. So there is no measured case here where BM25F beats
BM25 *after both are tuned*. The ArguAna 0.344 stands as an **un-tuned** comparison: whether it
survives a tuned BM25 is untested, and running that grid on 1406 queries was not affordable here.

**What the tuner adds is the negative answer, which is the useful one.** `Bm25FParameterTuner` reports
`WeightingHelped = false` on the reference corpus: it searched the title weight and could not beat
leaving it neutral. That is the same conclusion the hand-picked weights reached, arrived at by
search instead of by guessing — and it is the instrument to re-run on your own corpus. The other
standing finding is unchanged: the title weight itself moves nDCG@10 by at most 0.003 across nine
corpus/weight combinations, and on ArguAna the heaviest weight was worse than none.

Full tables are in [the eval harness README](bench/LexiSharp.Eval/README.md); reproduce the reference
corpus numbers with `--configs bm25,bm25-tuned,bm25f,bm25f-title,bm25f-tuned --top-k 5`.

The reference corpus moves the same way, and for a boring reason. Indexing its titles (see below)
lifts plain BM25 from 0.8359 to 0.8751 nDCG@5, while BM25F sits at 0.8189 and its title-2.0 variant
at 0.8248 — the weighting helps BM25F by 0.006 there and still does not reach BM25.

What *is* verified about the scorer, rather than inferred: the arithmetic, the term-overlap
contract, the plan parity, the explanation summing back to the score, and a test that a long body
the term never appears in does *not* change the score under BM25F while it does under
`Bm25Scorer`.

### Proximity

BM25 scores a document by *how often* the query terms occur and is blind to *where*. `quick fox` in
« the quick brown fox jumps over the lazy dog » scores exactly what it scores when the two words sit
in the same sentence. `ProximityReranker` is the second stage that notices the difference, using the
term positions the index already stores.

It is a **reranker**, not a scorer, on purpose: it refines a shortlist the first stage already deemed
relevant, and never promotes a document the first stage rejected. The first stage does the recall
work; this only reorders.

The measure is the **minimum window**. With `n` distinct query terms, `W` is the smallest number of
consecutive positions containing an occurrence of each, and `tightness = n / W` — `1.0` when the
terms are contiguous, smaller as they spread. Two shapes turn that into a score, and they are **not**
interchangeable:

```csharp
// Damp: multiply. Can only lower a score, down to the floor at worst.
new ProximityReranker(index, tokenizer, strength: 1.0, mode: ProximityMode.Damp, floor: 0.5)

// Boost: add strength × Σ idf(t) × tightness. The proximity term lives on its own scale,
// so it can only raise a score and never demotes on distance alone.
new ProximityReranker(index, tokenizer, strength: 1.0, mode: ProximityMode.Boost)
```

`strength = 0` is a no-op in both, and so is a `floor` of 1. A single-term query is always a no-op,
and a candidate missing one of the query terms is **left untouched** — whether it should match every
query term is the first-stage scorer's judgement, not this one's. Field boundaries count as distance,
since separate field runs are separated by a position gap.

**The floor is not a knob, it is a bug fix.** The obvious decay `1 - strength × (1 - n/W)` is
unbounded: two terms 81 positions apart in a 100-token document give `n/W ≈ 0.025`, a 97.5 % penalty,
and at opposite ends of a 10 000-token document `n/W ≈ 0.0002`. That punishes a document for being
*long*, when « are these terms near each other? » is a relative question. The first version had no
floor and cost 0.15 nDCG@5 on the reference corpus. The unit tests did not catch it, because they
asserted that an adjacent match outranks a spread one — which holds just as happily under a 2 %
penalty as under a 97 % one.
`TheDampPenaltyIsBoundedRegardlessOfDocumentLength` now pins it.

**And the measurement:**

| Config | reference (nDCG@5) | NFCorpus (nDCG@10) | SciFact (nDCG@10) |
|---|---|---|---|
| BM25 | **0.8751** | **0.308** | **0.662** |
| BM25 (tuned) | 0.8812 | 0.311 | 0.664 |
| proximity, damp s=0.25 | 0.8751 | 0.308 | 0.660 |
| proximity, damp s=1 | 0.8583 | 0.302 | 0.653 |
| proximity, boost s=1 | 0.8751 | 0.308 | 0.662 |

**Damp still loses, but by 0.017 rather than the 0.152 the unbounded version cost** — most of what
that earlier number reported was the bug, not the idea. Boost stays neutral: it moves scores without
moving the ranking, and on NFCorpus it is a hair better on MRR (0.519 vs 0.516) at identical nDCG.

The likely reason neither helps: nDCG@10 here is decided by whether the right document makes the top
ten at all, and BM25's term-frequency signal already orders that set well, so proximity only
reshuffles documents whose scores are already close. The gains it is credited with in the literature
come from exact-phrase tasks and from a tuned phrase *clause* as a separate scoring term, not from
one global strength applied after the fact.

So: **do not assume proximity helps.** It ships because the arithmetic is specified, tested, and
cheap to evaluate on your own corpus — `--configs bm25,bm25-proximity,bm25-proximity-boost`. It is
the right tool when a spread-out match really is a weaker match, which is a property of your data.

### BM25 variants

Two published single-field variants of BM25, both behind the same `ITextSearchEngine` contract, both
with an `IScoreExplainer` breakdown, a query plan, and a `delta` lower bound on the term frequency.

**BM25+** (Lv & Zhai 2011) replaces BM25's hard term-presence cut with a lower-bounded one, so a
document matching *more distinct* query terms gains over one repeating a single term many times:

```
score(q,d) = Σ_t  idf(t) · (k1 + 1) · (tf(t,d) + δ) / (k1 · (1 − b + b·|d|/avgdl) + tf(t,d) + δ)
```

**BM25L** (Lv et al. 2006) uses a *compressed* term frequency in the numerator and the raw one in the
denominator. That asymmetry is the variant:

```
ctd(t,d) = tf(t,d) / (1 − b + b·|d|/avgdl)
score(q,d) = Σ_t  idf(t) · (k1 + 1) · (ctd(t,d) + δ) / (k1 · (1 − b + b·|d|/avgdl) + tf(t,d))
```

One departure in both, and it is load-bearing: read literally these formulas give a positive
contribution to a term a document does **not** contain, which would make every document match every
query and break the « score 0 means no match » convention. So both only sum over terms the document
actually has, which is what keeps them `ITermOverlapScorer`. A test pins that a `delta` of 100 still
leaves an unrelated document at exactly 0.

`Bm25PlusScorer` degenerates to `Bm25Scorer` at `delta = 0`, and a test asserts that bit for bit
across four parameter pairs — the only reliable check that a published formula was transcribed
correctly rather than merely plausibly. BM25L does **not** degenerate to BM25 even at `delta = 0`,
because of the numerator/denominator asymmetry; its test asserts well-formedness instead.

**And the measurement, which is negative.** Against **tuned** BM25, not the default:

| Config | reference (nDCG@5) | NFCorpus (nDCG@10) | SciFact (nDCG@10) |
|---|---|---|---|
| BM25 (default) | 0.8751 | 0.308 | 0.662 |
| **BM25 (tuned)** | **0.8812** | **0.311** | **0.664** |
| BM25+ (δ=1.0) | 0.8644 | 0.300 | 0.663 |
| BM25L (δ=0.5) | 0.8073 | 0.257 | 0.575 |

**Neither beats a tuned BM25 anywhere.** BM25+ is within 0.001 of it on SciFact, which on 300 queries
is noise. BM25L is clearly worse, and badly so on the two BEIR corpora — 0.257 against 0.311 on
NFCorpus, 0.575 against 0.664 on SciFact.

**The obvious objection, stated before you make it:** the variants were measured at a *default* `δ`
while BM25 got tuned, so this is not a fair fight. Checked directly, and the verdict survives — against
**untuned** BM25 the same run reads 0.8751 / 0.8644 / 0.8073, and on NFCorpus 0.308 / 0.300 / 0.257.
So the variants lose on the merit of the formula, not only because the baseline got a tuning
advantage. What remains genuinely untested is whether a *tuned* `δ` would change that:
`Bm25ParameterTuner` only searches `(k1, b)` and there is no `δ` tuner, which is the honest caveat
and the obvious next step. BM25L is 13–17 % behind, though, which is a lot for a missing knob.

They ship because the formulas are well-defined, cheap, correctly transcribed, and measured — and
because a negative result with a number attached is worth more than an omission. Re-measure with
`--configs bm25,bm25-tuned,bm25+,bm25l` on your own corpus before dismissing them.

### Tuning BM25F

`Bm25FParameterTuner` grid-searches `(k1, b)` and the weight of each named field, in two stages:
`(k1, b)` first with fields neutral, then the weights at that winner. That is coordinate descent,
not an exhaustive product, and the class says so — a configuration that is only good jointly can be
missed. The run is refused above a configuration cap, with the count, rather than silently trimmed.

```csharp
var result = new Bm25FParameterTuner(index, validationQueries).Tune(
    weightedFields: ["title"],
    weightValues: [1.0, 1.5, 2.0, 3.0],
    topK: 10,
    metric: TuningMetric.Ndcg);

if (result.WeightingHelped)
    Console.WriteLine($"title weighs {result.Parameters.FieldWeights["title"]}");
else
    Console.WriteLine($"no weighting beat neutral; best was k1={result.Parameters.K1} b={result.Parameters.B}");

var engine = new RankedTextSearchEngine(index, new Bm25FScorer(result.Parameters));
```

Two things it deliberately reports rather than hides:

- **`WeightingHelped` is the answer to "does weighting help on my corpus".** It is `false` on the
  reference corpus and on all three BEIR corpora tried. `1.0` belongs in `weightValues` for exactly
  this reason: without a neutral candidate the search cannot conclude that nothing helps.
- **The best score is an oracle, not a fair baseline.** The winner was fitted on the same queries it
  is scored on, and the more configurations were tried the more of the gain is fitting noise. Score
  the result on a held-out set or treat it as an upper bound. `Bm25Tuned` has carried this caveat
  all along; this one does too.

Reuse the same `Bm25ValidationQuery` input as `Bm25ParameterTuner` — a single validation type for
both, deliberately.

**Without a field-aware index it refuses, by name.** Scoring against an index whose
`HasFieldStatistics` is `false` throws `NotSupportedException` naming the index rather than ranking
on zeros:

```csharp
// Throws: "BM25F needs per-field statistics, and FlatIndex has none…"
new Bm25FScorer().Score("1", ["ranking"], someIndexWithoutFields);
```

### Explainable scoring and BM25 tuning

Audit any ranking decision term by term, then let the corpus pick its own parameters. The
explainers cover the additive scorers — BM25, TF-IDF and query likelihood
(`QueryLikelihoodScorer` also exposes the collection-model contribution of query terms the
document does not contain); a pure filter like `BooleanScorer` has no additive breakdown and
`Explain` returns `null` for it:

```csharp
var engine = new RankedTextSearchEngine(index, new TfIdfScorer());
ScoreExplanation? why = engine.Explain("doc-1", "search engine");
// why.Terms -> per-term TF, IDF and score contribution; why.LengthRatio, why.Parameters...

var bm25 = new RankedTextSearchEngine(index, new Bm25Scorer());
ScoreExplanation? whyBm25 = bm25.Explain("doc-1", "search engine");

var tuner = new Bm25ParameterTuner(index, validationQueries: [
    new Bm25ValidationQuery("search engine", ["doc-1", "doc-7"]),
    new Bm25ValidationQuery("fuzzy matching", ["doc-3"]),
]);
Bm25TuningResult tuning = tuner.Tune(topK: 5);          // grid search over k1 x b
var tunedEngine = new RankedTextSearchEngine(index, new Bm25Scorer(tuning.Parameters));
```

Each validation query lists the relevant document ids; candidates are judged with
`Precision@k`, `Recall@k`, `F1@k` (default) or `nDCG@k` (`RetrievalMetrics`), averaged over the
set. The index is never mutated; `tuning.Grid` exposes every evaluated `(k1, b)` point.

### PostgreSQL backend (`LexiSharp.Postgres`)

Persistent, shared, concurrent search on top of a classic PostgreSQL setup. Several engines,
all implementing `ITextSearchEngine` and sharing the same documents table (so the hybrid
engine can fan out and merge lexical + vector + fuzzy results with
`ReciprocalRankFusionMerger`):

**Lexical (`PostgresTextSearchEngine`)** — full-text over `tsvector`:

```csharp
// install once:  dotnet add package LexiSharp.Postgres
using LexiSharp.Postgres;

ITextSearchEngine engine = new PostgresTextSearchEngine(
    "Host=db;Port=5432;Username=app;Password=secret;Database=search");

engine.Add(new SearchDocument("1", "the quick brown fox jumps over the lazy dog",
    new Dictionary<string, string> { ["kind"] = "fable" }, "fable"));
```

The provider installs (idempotently) the `unaccent` extension, a documents table with a
`tsv tsvector` column and a GIN index, then queries it with `websearch_to_tsquery` and ranks
with `ts_rank_cd`. Combined with the `simple` config, `unaccent` mirrors LexiSharp's
accent-insensitive normalization. Scores are PostgreSQL-native, so they are **not** numerically
comparable to `Bm25Scorer`/`TfIdfScorer` — feed both backends into the hybrid engine below
when you need one consistent ordering.

**Vector (`PostgresVectorSearchEngine`)** — ANN over `pgvector` (HNSW or IVFFlat), fed by
your own embeddings:

```csharp
ITextSearchEngine vector = new PostgresVectorSearchEngine(
    "Host=db;Port=5432;Username=app;Password=secret;Database=search",
    new MyEmbeddingProvider(),                                // your ONNX/model-server deps, never LexiSharp
    new PostgresVectorOptions { Dimension = 384, Distance = VectorDistance.Cosine });

vector.Add(new SearchDocument("1", "the quick brown fox ..."));
vector.Search("a fast fox");   // scores: cosine→1-dist, L2→1/(1+dist), inner product→-dist
```

The engine installs (idempotently) the `vector` extension, adds an `embedding vector(D)`
column and an HNSW (or IVFFlat) index on the same documents table. IVFFlat needs rows to
cluster lists, so the index is created on the first `EnsureSchema()` call **after** your
first inserts. ANN results are approximate: combine with `HybridTextSearchEngine` + RRF to
trade recall for speed — exact cosine behavior is verified in the integration suite.

The embedding seam is role-aware: `PostgresVectorSearchEngine` embeds indexed documents with
`EmbeddingUse.Passage` and queries with `EmbeddingUse.Query`, so asymmetric models (E5 prefixes
and friends) work through the same single provider method. Finer knobs live in the options
(see the class docs): `HnswEfSearch` (per-search candidate list; the engine wraps the query in
a `SET LOCAL hnsw.ef_search` transaction), `EmbeddingTextField` (embed a named
`SearchDocument.TextFields` entry instead of `Text`, leaving `content`/`tsv` untouched for the
lexical engine) and, for a title/description (or any multi-section) split,
`EmbeddingColumns` — one `{suffix}_embedding vector(D)` column and ANN index per configured
`TextFields` entry. A search then targets a subset of columns
(`SearchWithColumns`) or all of them, OR-fusing candidates by best per-column similarity, and
`SearchWithDetails` (`IDetailedSearchEngine`) exposes each column's contribution for UI badges
or telemetry. To encode the same query differently per column (e.g. center each channel's
query in its own space), implement `IColumnAwareEmbeddingProvider`: the engine then passes the
column label on every query and passage call; a plain `IEmbeddingProvider` keeps the single
query vector shared across columns. The engine also implements `IListableSearchEngine`, so the
full set of stored ids can be streamed (`ListDocumentIds` / `ListDocumentIdsAsync`, keyset
pagination) to diff against an external ledger.

By default the integration tests are skipped unless `POSTGRES_TEST_CONNECTION` points at a
live instance (e.g. `Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=lexisharp`).
The vector and sparse tests additionally require the `vector` extension: use the
`pgvector/pgvector:pg16` image (lexical tests only need stock PostgreSQL).

**Sparse (`PostgresSparseSearchEngine`)** — learned-sparse ANN over `pgvector sparsevec`
(HNSW only — IVFFlat is unavailable for `sparsevec`), fed by your own sparse model:

```csharp
ITextSearchEngine sparse = new PostgresSparseSearchEngine(
    connectionString,
    mySpladeProvider,                                       // ISparseEmbeddingProvider, never LexiSharp
    new PostgresSparseOptions
    {
        Vocabulary = vocabulary,                            // term → coordinate, fixed up front
        Distance = SparseDistance.InnerProduct,             // default; dot product suits SPLADE
    });

sparse.Add(new SearchDocument("1", "the quick brown fox ..."));
sparse.Search("a fast fox");   // scores: inner product→-dist, cosine→1-dist, L2/L1→1/(1+dist)
```

The engine installs (idempotently) the `vector` extension, adds a `sparse sparsevec(D)` column
and an HNSW index on the same shared documents table. Because `sparsevec` is a positional
format, the **vocabulary (term → coordinate) is an index-layout decision**: it must be fixed
once and shared between the provider at index time and the one at query time. Terms outside the
vocabulary are ignored; a query with no known term returns nothing. HNSW — unlike IVFFlat —
works on empty tables and supports inserts, so there is no "index after first batch" step.
`sparsevec` caps a vector at 1000 non-zero elements. Scores are PostgreSQL-native and again
depend on the chosen distance; route through `ReciprocalRankFusionMerger` when mixing with the
lexical engine.

**Fuzzy (`PostgresFuzzySearchEngine`)** — approximate, typo-tolerant matching over `pg_trgm`
trigrams, with optional `fuzzystrmatch` (edit distance + phonetics):

```csharp
ITextSearchEngine fuzzy = new PostgresFuzzySearchEngine(connectionString, new PostgresFuzzyOptions
{
    SearchMode = TrgmSearchMode.Nearest,        // kNN: closest labels first (autocomplete)
    // SearchMode = TrgmSearchMode.Similarity,  // threshold: content % query (de-dup, did-you-mean)
    SimilarityThreshold = 0.3,                  // honored via set_limit() in Similarity mode
    UseLevenshteinRefinement = true,            // exact edit-distance post-filter
    IncludePhonetic = true,                     // metaphone column; phonetic matches (Similarity mode)
});
fuzzy.Index(new[]
{
    new SearchDocument("1", "katherine"),
    new SearchDocument("2", "catherine"),
});
fuzzy.Search("caterin");   // typo-tolerant: both labels come back
```

The engine installs (idempotently) `pg_trgm` (+ `fuzzystrmatch` when enabled) and **GiST and
GIN trigram indexes** on the same shared documents table. Scores are trigram similarities in
`[0, 1]` (1 identical, 0 no shared trigram → excluded, honoring the library's score-0
convention). `Nearest` mode orders with the GiST kNN operator (`content <-> query`); `Similarity`
mode ranks by `similarity()` above the configured threshold. These are the classic building
blocks for autocomplete, de-duplication of names/addresses and "did you mean". PostgreSQL-native
scores again call for `ReciprocalRankFusionMerger` when mixing with other engines.

### ParadeDB BM25 backend (`ParadeDBTextSearchEngine`)

Okapi BM25 ranking computed by Tantivy inside PostgreSQL through the `pg_search`
extension — an option to consider when `ts_rank_cd` ranking is not good enough and true
BM25 is wanted. It ships in the same `LexiSharp.Postgres` package as the other PostgreSQL
engines; only the `pg_search` extension (a self-hosted install or the ParadeDB image) is
required server-side.

```csharp
// install once:  dotnet add package LexiSharp.Postgres
using LexiSharp.ParadeDB;

ITextSearchEngine engine = new ParadeDBTextSearchEngine(connectionString);
engine.Add(new SearchDocument("1", "the quick brown fox jumps over the lazy dog"));
```

The engine installs (idempotently) the `pg_search` extension and a ParadeDB index
(`USING paradedb`, the renamed `USING bm25`) **on the same shared documents table** as the
Postgres engines, then matches with the `|||` disjunction operator and ranks with
`pdb.score(id)` — real BM25 (Tantivy variant), unlike `ts_rank_cd`. The default content
tokenizer (`pdb.simple` with ASCII folding) mirrors LexiSharp's diacritic-insensitive,
lowercase normalization:

```csharp
new ParadeDBTextSearchEngine(
    connectionString,
    new ParadeDBOptions { ContentTokenizer = "pdb.simple('ascii_folding=true')" });
```

BM25 scores are PostgreSQL-native, so they are **not** numerically comparable to
`Bm25Scorer`/`TfIdfScorer` — use the hybrid engine's `ReciprocalRankFusionMerger` (or
re-rank) for a single cross-engine ordering. Note the extension is **AGPL-3 licensed**: fine
for SaaS/internal use, but review it if you distribute the stack.

By default the tests are skipped unless `POSTGRES_TEST_CONNECTION` points at a live instance
**with `pg_search` available** (the `paradedb/paradedb:pg16` image ships it, preloaded) —
they self-skip when the extension is absent.

### Hybrid engine

Federate a **hot** in-memory index and a **cold** persistent backend, and produce one
consistent global ranking:

```csharp
using LexiSharp.Core;
using LexiSharp.Hybrid;
using LexiSharp.Indexing;
using LexiSharp.Ranking;

ITextSearchEngine hybrid = new HybridTextSearchEngine(new ITextSearchEngine[]
{
    new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer()), // hot subset
    postgresEngine,                                                       // cold backend
});

IReadOnlyList<SearchResult> results = hybrid.Search("textual search");
```

`HybridTextSearchEngine` queries every engine, de-duplicates the candidates by document id,
then re-ranks the whole union with a single scorer (`RerankingResultMerger`, default BM25).
Writes fan out to every engine. Three merge strategies are available:

| Merger | Behavior | Best for |
| --- | --- | --- |
| `RerankingResultMerger` (default) | re-scores the union with one `ITextScorer` | comparable stats, identical score scale wanted |
| `ReciprocalRankFusionMerger` | `Σ 1/(k + rank)` (k=60), rank-only | engines with **incomparable scales** — lexical + vector, ts_rank_cd vs BM25 (Postgres vs ParadeDB vs in-memory) |
| `WeightedScoreResultMerger` | normalized per-engine score blend | native scores trusted, per-engine weights wanted |
| `CombSumResultMerger` | sum of normalized per-engine scores | scores (not just ranks) are meaningful and should add up |
| `CombMNZResultMerger` | CombSUM × number of engines that returned the doc | reward cross-engine **agreement** |

Reciprocal Rank Fusion never looks at scores, so it bridges engines whose scores are not
comparable — the sparse and dense embedding backends land in the same formula without
calibration.

For score breakdowns, `SearchWithDetails()` returns `DetailedSearchResult`s where each
document also carries its raw per-source score (`Contributions`), keyed by the labels passed
as `sourceNames` to the constructor (`"lexical"`, `"semantic"`, ... — default `"engine-N"`).
A source that did not return the document is simply absent from that dictionary; the merged
ordering from `Search()` is unchanged.

**Embeddings are an agreed seam, not a feature here**: `IEmbeddingProvider` (core) describes how a
consumer project (ONNX model, model server, ...) would produce vectors — LexiSharp never
computes embeddings — and `VectorSimilarity` provides pure cosine math. `PostgresVectorSearchEngine`
is the reference consumer: it turns any provider into an ANN backend that the same
`HybridTextSearchEngine` merges exactly like a lexical engine.

### Sparse learned embeddings (`ISparseEmbeddingProvider`, `SparseTextSearchEngine`)

SPLADE-style models (SPLADE, uniCOIL, ...) produce **sparse** learned vectors: a handful of
`term → weight` pairs where the weights are learned instead of tf-idf/BM25 frequencies.
The key insight of this family is that it does **not** replace the inverted-index infrastructure,
only the scoring function. `SparseTextSearchEngine` applies exactly that: it keeps a classic
`term → document → weight` inverted index, and a query scores each document by sparse
**dot product** `Σ_t w_q(t)·w_d(t,d)` — only the terms the learned model activated are visited:

```csharp
using LexiSharp.Core;
using LexiSharp.Indexing;

ISparseEmbeddingProvider splade = myOnnxSplade; // consumer-provided, incl. vocab mapping
ITextSearchEngine engine = new SparseTextSearchEngine(splade);

engine.Add(new SearchDocument("doc-1", "sparse retrievers beat dense on exact terms"));

var results = engine.Search("learned sparse retrieval");
```

Like `IEmbeddingProvider`, `ISparseEmbeddingProvider` is a pure seam in the core: the ONNX model,
tokenizer and vocabulary live in the consumer — and the `EmbeddingUse` role is threaded through
uniformly across the dense/sparse/token seams (`Passage` at index time, `Query` per search),
so asymmetric sparse variants keep a hook even though most SPLADE models are symmetric.
A walkthrough of writing a SPLADE provider
(ONNX + HuggingFace tokenizer + vocabulary mapping) is in
[docs/SPLADE.md](docs/SPLADE.md) — an outline, not a tested reference implementation. Weights
are expected non-negative (ReLU-like); non-positive values are treated as "term absent". The
engine implements `ITextSearchEngine`, so
it drops straight into `HybridTextSearchEngine` where it merges with BM25 and dense engines via
`ReciprocalRankFusionMerger` — RRF keeps sparse-only hits (matching terms the lexical scorer and
the dense cosine disagree on) that a BM25 re-scoring merge would drop.

The engine never re-embeds on reload: `Export()` / `Import()` decouple inference from
persistence, `MessagePackSparseIndexPersistence` serializes the stored weights directly, and
`PostgresSparseSearchEngine` is the same model over `pgvector sparsevec`. In every backend, only
**queries** keep needing the provider after the corpus is loaded.

### In-memory dense retrieval (`InMemoryVectorSearchEngine`, `HashingEmbeddingProvider`)

The dense counterpart of the sparse engine, and the in-process twin of the PostgreSQL
`pgvector` engine: documents are embedded with an `IEmbeddingProvider` and queries are ranked
by **cosine similarity** over the whole corpus — a linear scan, no ANN index, so it targets
small/medium corpora (reach for `LexiSharp.Postgres` beyond that). Negative and orthogonal
cosines are clamped to `0` ("not a match"), keeping the standard LexiSharp score convention:

```csharp
using LexiSharp.Core;
using LexiSharp.Indexing;

IEmbeddingProvider model = myOnnxMiniLm;               // consumer-provided
ITextSearchEngine dense = new InMemoryVectorSearchEngine(model);

dense.Add(new SearchDocument("doc-1", "oauth access token renewal"));

var results = dense.Search("refresh credentials");
```

Like the sparse engine it implements `ITextSearchEngine`, so it fuses with BM25 through
`HybridTextSearchEngine` + `ReciprocalRankFusionMerger` (lexical + dense hybrid, in memory),
and `Export()` / `Import()` reload a corpus from its stored vectors without calling the model.
`InMemoryVectorSearchEngine` also implements `IQueryCostProbe` (a scan touches every document),
so `RoutedSearchEngine` can weigh it against the lexical engines.

To make the seam usable with **no model at all**, the core ships
`HashingEmbeddingProvider`: a deterministic feature-hashing embedding (FNV-1a, sign hashing,
L2-normalized) implementing both `IEmbeddingProvider` and `ITokenEmbeddingProvider`. It is
**not semantic** — it captures lexical overlap, not meaning — but it exercises and tests the
whole embedding stack (dense search, MMR, MaxSim, Postgres vector) offline, and is a drop-in
baseline to swap for a real model behind the same interface.

### Reranking stage

The reranking stage composes with all of it: wrap the hybrid in a
`RerankedTextSearchEngine` (core decorator) and pass a `CascadeRerankPipeline`, a
`MaximalMarginalRelevanceReranker`, a `CrossEncoderReranker` or a `MaxSimReranker` to add a
precision or diversity pass on top of the fused ranking — the same two-stage
retrieve-then-rerank shape, one line of composition.

## Architecture

```
Package            Responsibilities
─────────────────────────────────────────────────────────────────────────────
LexiSharp         records + interfaces + in-memory index + scorers + tokenizer + IEmbeddingProvider + ISparseEmbeddingProvider + sparse engine + in-memory dense engine + hashing embedding provider (export/import) + boost/rerank decorators + filters + highlighting + facets + similarity + keywords + metrics + hybrid federation (RRF, weighted, cascade, cross-encoder, MMR, MaxSim)
LexiSharp.Postgres  PostgreSQL providers: tsvector+unaccent (lexical), pgvector ANN (vector), pgvector sparsevec (sparse), pg_trgm+fuzzystrmatch (fuzzy), pg_search/Tantivy (true BM25)
LexiSharp.MessagePack   MessagePack (binary) persistence for the in-memory index and the sparse engine
```

Within the core package, separation of concerns mirrors the recommendations the library
was designed from:

- the **index** owns corpus statistics (tf, df, document length, positions, vocabulary, and the
  per-field statistics when a document declares `TextFields`);
- the **scorer** is a pure strategy reading from the index;
- the **engine** orchestrates query tokenization, scoring, filtering and ranking.

### Scoring conventions

- A score of exactly `0` means *not a match* and the document is excluded from results
  (all built-in scorers honor this).
- Every sub-system is culture-agnostic; text is normalized to lowercase without accents
  so that `"Résumé"` and `"resume"` match.

## Building & testing

```bash
dotnet build LexiSharp.slnx
dotnet test  tests/LexiSharp.Tests                # xUnit suite (Postgres tests need POSTGRES_TEST_CONNECTION)
dotnet run  --project bench/LexiSharp.Benchmarks  # BenchmarkDotNet suite (published numbers: BENCHMARKS.md)
```

Postgres/ParadeDB integration tests run against whatever `POSTGRES_TEST_CONNECTION` points to:
`pgvector/pgvector:pg16` covers the lexical + vector + sparse + fuzzy suites,
`paradedb/paradedb:pg16` covers the lexical + ParadeDB (BM25) + fuzzy suites. The sparse tests
self-skip when the `vector` extension is unavailable. The fuzzy tests
self-skip when `pg_trgm` (and `fuzzystrmatch`, when exercised) are unavailable.

## License

MIT