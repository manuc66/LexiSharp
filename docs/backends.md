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
installation, the query paths, and exact cosine behaviour for the vector engine. Retrieval
*quality* is measured for the three engines that rank on their own: the evaluation harness's
`--backend lexical|paradedb|vector` adds a nDCG row for the same BEIR corpus, queries and qrels as
the in-memory table, and [the evaluation page](evaluation.md#the-sql-backends-on-the-same-corpora)
carries the figures and what each one is comparable with. What is **not** measured against a public
benchmark is the **sparse** and **fuzzy** engines: no sparse model is wired into the harness, and
trigram similarity is not a ranking of prose, so the integration tests are what cover them.
PostgreSQL-native scores are also **not** numerically comparable to
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

**Field weighting.** By default every text field is flattened into one `tsvector` at the same
weight, so a match in `title` and a match in `content` contribute the same. `FieldWeights` maps a
`SearchDocument.TextFields` entry to one of PostgreSQL's four weight classes and writes a weighted
vector instead:

```csharp
var options = new PostgresIndexOptions
{
    FieldWeights = new Dictionary<string, PostgresFieldWeight>
    {
        ["title"] = PostgresFieldWeight.A,
        ["summary"] = PostgresFieldWeight.B,
    },
    DefaultFieldWeight = PostgresFieldWeight.B,   // content and unlisted fields
};

var engine = new PostgresTextSearchEngine(connectionString, options);
```

The `tsv` column becomes the concatenation of one `setweight(to_tsvector(...), class)` per section
— the content, the fields the map does not name, and each named field. The lexical engine passes
`ts_rank_cd` the matching class-to-value array, so the classes are read back as PostgreSQL's
defaults: `A = 1.0`, `B = 0.4`, `C = 0.2`, `D = 0.1`. With the example above a title match counts
2.5 times a body match. `FieldWeights` unset (the default) or empty produces exactly the expression
a table has always been written with — one `to_tsvector` call over `content || ' ' || text_fields`
— and `ts_rank_cd` is called without a weights array, so an existing index neither changes its DDL
nor its scores.

What this is not: a per-field scoring model. `ts_rank_cd` is coverage-based and has no per-field
length normalisation, so a match in a long field contributes the same as a match in a short one and
only the class separates them. A field the map does not name is still indexed, in the unlisted
section at `DefaultFieldWeight`; naming one field does not narrow the index to it.

The weighting lives in the rows, not in the table definition: `tsv` is an ordinary column that the
engines write, so `EnsureSchema` has nothing to alter and an existing table needs no DDL migration.
Rows written before the option was set keep their unweighted vector, and the only way to bring them
in line is to rewrite them — the statement below is the expression `PostgresSchema.TsvExpression`
builds for `title = A` over the default classes, and the GIN index maintains itself through the
`UPDATE`:

```sql
UPDATE public.lexisharp_documents AS d
SET tsv = setweight(to_tsvector('simple', unaccent(d.content)), 'D')
       || setweight(to_tsvector('simple', unaccent(coalesce(
              (SELECT string_agg(value, ' ' ORDER BY key)
               FROM jsonb_each_text(d.text_fields)
               WHERE key NOT IN ('title')), ''))), 'D')
       || setweight(to_tsvector('simple', unaccent(coalesce(d.text_fields ->> 'title', ''))), 'A');
```

A row at class `D` is stored exactly like an unweighted one (the class letter is omitted), so this
rewrite is the only reliable moment at which the whole table is on one configuration. The vector
and sparse engines write the same `tsv` from the same expression, which is why the option exists on
`PostgresVectorOptions` and `PostgresSparseOptions` too: on a table served by several engines, all
of them have to be given the same weights, or the same document scores differently depending on
which engine wrote it. Their `Insert`s write `tsv` on the way in as well as on `ON CONFLICT`, so a
table populated by the vector engine is searchable through the lexical engine.

**Measured effect.** `bench/LexiSharp.Eval`'s lexical row takes the weighting with
`--backend-field-weights`. Across the two corpora the harness has a lexical row for, weighting the
title is nearly inert: nDCG@10 moves by at most 0.0009, and SciFact does not move to four decimals.
Measured on one host in one session, `simple` configuration, harness defaults (exponential gains),
one run per row; the unweighted row was run twice and reproduced its four decimals exactly:

```bash
POSTGRES_TEST_CONNECTION="Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=lexisharp"
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset nfcorpus --no-tuned \
  --backend lexical --backend-connection "$POSTGRES_TEST_CONNECTION" \
  --backend-field-weights title=A
```

| Corpus | unweighted | `title=A` (other text `D`) | `title=B` (other text `D`) |
|---|---:|---:|---:|
| NFCorpus (323 queries) | 0.1864 | 0.1869 | 0.1873 |
| SciFact (300 queries) | 0.0188 | 0.0188 | 0.0188 |

The direction is consistent with the title carrying a little signal on NFCorpus and none on a
corpus whose queries are long enough that `websearch_to_tsquery`'s AND gate, not the ranking,
decides the match set. A difference this small on one host is a measurement of this configuration,
not a claim that field weighting does not work: the classes are coarse (four buckets, fixed
values), and the engine has no per-field length to normalise against.

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
first inserts. What is approximate is **which** documents come back, not their scores. pgvector
evaluates the distance of every row it returns with the same operator that ordered them, so the
page arrives ordered and scored exactly and re-scoring it would change nothing — the score
expression and the `ORDER BY` are one switch on `Distance`. The cost of the index is recall: a
true neighbour the graph never visited. Raise `HnswEfSearch` to spend more I/O on finding it, or
combine with `HybridTextSearchEngine` + RRF so a lexical channel covers what the ANN missed —
which is the recall you are trading away for speed. Exact cosine behavior is verified in the
integration suite.

**When PostgreSQL stops using the index.** `hnsw.ef_search` feeds pgvector's planner hook,
which grows the ANN index's estimated *startup* cost with it. Past a threshold set by your
corpus's size and statistics, the planner answers the search with a sequential scan and an
exact top-k instead: the same rows, exact rather than approximate, and on a corpus the index
was never needed, markedly slower. Measured on NFCorpus (3,633 documents, 384d, `m = 16`, table
analyzed), the index answers up to `ef_search = 60` and a scan takes over from 80 — at
`ef_search = 160` the scan's median query is 14.39 ms against the index's 1.90 ms, over the
dataset's 323 test queries, and the extra exactness is 0.991 recall against 1.000. At 50,862
documents the index answers at every `ef_search` measured (10 to 640), and there an exact scan
costs 82-89 ms against the index's 0.43 ms at `ef_search = 10`. So the setting is yours to keep
inside the regime where the index wins, and `EXPLAIN (ANALYZE)` is how you read back which one
answered: they differ in latency severalfold while their recall differs by a point or two, so
latency alone cannot tell an index from a scan.

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
