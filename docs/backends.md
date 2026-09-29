---
title: PostgreSQL and ParadeDB backends
nav_order: 9
description: >-
  Full-text search on tsvector, pgvector nearest-neighbour search, sparse
  retrieval, pg_trgm fuzzy search, and true BM25 on ParadeDB pg_search.
---

# PostgreSQL and ParadeDB backends

The core is in-memory. Persistence and scale are separate packages, and every one of them
implements the same `ITextSearchEngine` contract, so a backend is a substitution rather
than a rewrite.

| Package | What it adds | Depends on |
|---|---|---|
| `LexiSharp` | records, interfaces, in-memory index, scorers, tokenizer, all decorators | **nothing** |
| `LexiSharp.MessagePack` | binary persistence for the in-memory index and the sparse engine | MessagePack |
| `LexiSharp.AspNetCore` | a `GET /search` minimal-API endpoint | ASP.NET Core |
| `LexiSharp.Postgres` | lexical, vector, sparse, fuzzy and BM25 engines over PostgreSQL | Npgsql |

`LexiSharp.Postgres` ships five engines over **one shared documents table**, picked at
instantiation:

- **Lexical** — `tsvector` + GIN + `unaccent`, ranked with `ts_rank_cd`.
- **Vector** — ANN over `pgvector` (HNSW or IVFFlat), fed by your own `IEmbeddingProvider`.
- **Sparse** — learned-sparse ANN over `pgvector sparsevec` (HNSW only), fed by your own
  `ISparseEmbeddingProvider`.
- **Fuzzy** — approximate typo-tolerant matching over `pg_trgm`, optionally refined with
  `fuzzystrmatch`.
- **BM25** — `ParadeDBTextSearchEngine`, true Okapi BM25 computed by Tantivy through
  `pg_search` rather than `ts_rank_cd`. **The extension is AGPL-3 licensed** — fine for
  SaaS and internal use, worth a review if you distribute the stack.

**What is verified, and what is not.** The integration tests run against a live PostgreSQL
in CI (`pgvector/pgvector:pg16` and `paradedb/paradedb:pg16`) and self-skip without
`POSTGRES_TEST_CONNECTION`, so what they cover is executed on every build: schema
installation, the query paths, and exact cosine behaviour for the vector engine. What they
do **not** cover is retrieval *quality* against a public benchmark — the BEIR numbers
elsewhere in this documentation come from the in-memory engines, and the SQL backends are
not in that harness. PostgreSQL-native scores are also **not** numerically comparable to
`Bm25Scorer`; feed several backends to the
[hybrid merger](pipelines.md#hybrid-engine) when you need one consistent ordering.

## PostgreSQL backend (`LexiSharp.Postgres`)

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

## ParadeDB BM25 backend (`ParadeDBTextSearchEngine`)

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
