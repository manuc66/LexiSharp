---
title: PostgreSQL and ParadeDB backends
nav_order: 9
description: >-
  Full-text search on tsvector, BM25 without an extension, pgvector
  nearest-neighbour search, sparse retrieval, pg_trgm fuzzy search, and true BM25 on ParadeDB
  pg_search.
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
| `LexiSharp.Postgres` | lexical, BM25, vector, sparse and fuzzy engines over PostgreSQL | Npgsql |

`LexiSharp.Postgres` ships six engines over **one shared documents table**, picked at
instantiation:

- **Lexical** — `tsvector` + GIN + `unaccent`, ranked with `ts_rank_cd`.
- **BM25 (stock PostgreSQL)** — `PostgresBm25SearchEngine`: BM25 over the term frequencies the
  `tsvector` column already stores, computed by `Bm25Scorer`. No extension beyond `unaccent`, so
  it runs where `pg_search` cannot be installed.
- **Vector** — ANN over `pgvector` (HNSW or IVFFlat), fed by your own `IEmbeddingProvider`.
- **Sparse** — learned-sparse ANN over `pgvector sparsevec` (HNSW only), fed by your own
  `ISparseEmbeddingProvider`.
- **Fuzzy** — approximate typo-tolerant matching over `pg_trgm`, optionally refined with
  `fuzzystrmatch`.
- **BM25 (ParadeDB)** — `ParadeDBTextSearchEngine`, true Okapi BM25 computed by Tantivy through
  `pg_search`. **The extension is AGPL-3 licensed** — fine for SaaS and internal use, worth a
  review if you distribute the stack.

**What is verified, and what is not.** The integration tests run against a live PostgreSQL
in CI (`pgvector/pgvector:pg16` and `paradedb/paradedb:pg16`) and self-skip without
`POSTGRES_TEST_CONNECTION`, so what they cover is executed on every build: schema
installation, the query paths, and exact cosine behaviour for the vector engine. Retrieval
*quality* is measured for the six engines that rank on their own: the evaluation harness's
`--backend lexical|bm25|paradedb|vector|fuzzy|sparse` adds a nDCG row for the same BEIR corpus,
queries and qrels as the in-memory table, and [the evaluation
page](evaluation.md#the-sql-backends-on-the-same-corpora) carries the figures and what each one is
comparable with. `--backend sparse` indexes the same precomputed SPLADE vectors as the in-memory
`--sparse` row — the same directory, the same loader — and reports two rows, the engine's HNSW
index and the same table with the index dropped, so what the index costs is separated from where
the dot product runs. The **in-memory** sparse engine's own row, and the vectors' provenance and
limits, are on [the evaluation page](evaluation.md#learned-sparse-splade-through-onnx).
PostgreSQL-native scores are also **not** numerically comparable to `Bm25Scorer`; feed several
backends to the [hybrid merger](pipelines.md#hybrid-engine) when you need one consistent ordering.

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
corpus whose queries are long enough for the AND gate to leave too little to rank for a weighting
to show through. A difference this small on one host is a measurement of this configuration,
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

**Match rule (`MatchMode`).** A multi-term query becomes a `tsquery` in one of two ways:

| mode | expression | a document must contain |
|---|---|---|
| `AllTerms` (default) | `websearch_to_tsquery` | every term |
| `AnyTerm` | one `tsquery` OR-ing the query's lexemes | any term |

`AnyTerm` takes its lexemes from `to_tsvector`, so they come from the column's own
configuration; it is a bag of words (the websearch operators `or`, `-term` and a trailing `*` are
not honoured) and quoted phrases are still hard gates. It changes which documents are candidates,
not how they are ranked — still `ts_rank_cd`.

On this repository's BEIR runs the rule is worth more than the ranking difference on long queries,
and close to nothing on short ones:

| corpus | `AllTerms` | `AnyTerm` |
|---|---:|---:|
| NFCorpus (queries average 3.4 terms) | 0.1864 | 0.1881 |
| SciFact (13.1 terms) | 0.0188 | 0.0988 |

SciFact is where the AND rule buries the corpus: requiring all thirteen terms of an abstract leaves
almost nothing to rank. NFCorpus barely moves, because three terms usually do co-occur — its
distance from BM25 is the ranker, not the rule. `AnyTerm` still lands far from BM25's 0.662 on
SciFact, which is what `PostgresBm25SearchEngine` (below) is for.

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
`sparsevec` caps a vector at 1000 non-zero elements. The engine exposes no option for
`hnsw.ef_search`, so a search runs at whatever the server's setting is (pgvector's default 40);
the measured row states the value it ran at. Scores are PostgreSQL-native and again depend on the
chosen distance; route through `ReciprocalRankFusionMerger` when mixing with the lexical engine.

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

**Measured quality.** The harness measures this engine on the same BEIR corpora, queries and qrels
as the other backends, with one row per similarity threshold
(`--backend fuzzy --backend-threshold 0.3,0.1,0.05,0.01,0.005,0.001,0.0001`; the command and the
other metrics are on [the evaluation page](evaluation.md#the-sql-backends-on-the-same-corpora)).
nDCG@10, harness defaults, title concatenated into the compared content:

| Corpus (queries) | default 0.3 | lower end of the sweep |
|---|---:|---:|
| NFCorpus (323) | 0.0000 | 0.0981 at 0.005 and below |
| SciFact (300) | 0.0033 | 0.2186 at 0.05 and below |

At the default threshold the lane finds almost nothing, and lowering it only recovers a fraction of
what BM25 scores on the same corpus (NFCorpus 0.308, SciFact 0.662). That is the property the mode
has by construction: a trigram similarity divides shared character trigrams by the sum of both
trigram sets, so a short query against a whole document scores near zero however relevant the
document is. The engine is a typo-tolerant matcher for short labels, de-duplication and "did you
mean" — not a ranking of prose, and the rows above are what that costs on a prose benchmark. They
are in-sample: each threshold was scored on the qrels it is reported against, so the best row is an
upper bound and not a tuned configuration. `Nearest` mode, the Levenshtein and phonetic refinements,
and `%>`/`word_similarity` are not measured.

## BM25 over stock PostgreSQL (`PostgresBm25SearchEngine`)

True Okapi BM25 with **no extension beyond `unaccent`**. The `tsvector` column and its GIN index do
the matching — the same table, the same DDL and the same `tsv` expression as the lexical engine —
and `Bm25Scorer` does the ranking over the term frequencies the vector already stores.

```csharp
using LexiSharp.Postgres;

ITextSearchEngine engine = new PostgresBm25SearchEngine(connectionString);
engine.Add(new SearchDocument("1", "the quick brown fox jumps over the lazy dog"));
engine.Search("quick fox");   // Bm25Scorer scores, not ts_rank_cd's
```

It exists because `pg_search` (next section) is the faster route to BM25 and the one with the
mature Tantivy index, but it needs a preloaded library, and no major managed service lets you
install one. This engine needs `CREATE EXTENSION unaccent` and nothing else, so on RDS, Cloud SQL
and Azure Database for PostgreSQL the only server-side step is allowing that one extension.

**Where the numbers come from.** `N`, the average document length and every query term's document
frequency are read once through `ts_stat` and cached; the term frequencies and the length come from
the candidate row's own `tsvector`. The query passes through the same text search configuration as
the column, so the scorer sums over the terms the index matched. Any write through the engine
invalidates the snapshot and the next search rebuilds it; a write by another engine on the shared
table (or a bulk load) does not, and `RefreshStatistics()` is how that caller says so. Scores are
on `Bm25Scorer`'s scale, unlike `ts_rank_cd`'s and `pdb.score`'s, so they are comparable with the
in-memory BM25 row.

**The candidate pool.** PostgreSQL selects the best `CandidatePool` matches by `ts_rank_cd`
(default 1,000; `0` removes the bound up to the engine's 100,000-row ceiling) and only those are
re-ranked. The pool bounds recall — a document outside it cannot be returned — and does not change
the ordering inside it. On the same BEIR corpora:

| corpus | pool 1,000 | pool 0 (exact) | in-memory BM25 | ParadeDB |
|---|---:|---:|---:|---:|
| NFCorpus | 0.2999 | 0.3072 | 0.308 | 0.3084 |
| SciFact | 0.5636 | 0.6665 | 0.662 | 0.6619 |

At the default pool the row is within 0.008 of the in-memory BM25 on NFCorpus and 0.10 short on
SciFact: on a corpus where a thirteen-term query matches half of it, the pool's 1,000 candidates are
the whole gap. Raise the pool, or set it to `0`, when the match sets are that large — the cost is
one `tsvector` per match transferred and parsed per query.

**What it costs, in milliseconds.** The evaluation harness reports quality and not time on purpose;
the [PostgreSQL lane](benchmarks.md#postgresql-lane-postgres-what-a-search-costs-on-stock-postgresql)
measures the other half — one process, one thread, a pooled data source, a page of 10, on a local
server:

| corpus (documents) | engine | stats build | p50 | p95 | mean |
|---|---|---:|---:|---:|---:|
| NFCorpus (3,633) | `ts_rank_cd`, all terms | — | 0.6 ms | 1.3 ms | 0.7 ms |
| NFCorpus | `ts_rank_cd`, any term | — | 4.1 ms | 74.6 ms | 21.3 ms |
| NFCorpus | BM25, pool 1,000 | 220 ms | 16.0 ms | 107.3 ms | 37.3 ms |
| NFCorpus | BM25, pool 0 | 201 ms | 15.8 ms | 177.1 ms | 58.1 ms |
| SciFact (5,183) | `ts_rank_cd`, all terms | — | 0.7 ms | 0.8 ms | 0.7 ms |
| SciFact | `ts_rank_cd`, any term | — | 170.1 ms | 347.2 ms | 177.6 ms |
| SciFact | BM25, pool 1,000 | 252 ms | 203.3 ms | 385.2 ms | 210.7 ms |
| SciFact | BM25, pool 0 | 246 ms | 299.7 ms | 490.0 ms | 301.0 ms |

- **The candidate selection is the cost, not the BM25.** On SciFact the `any term` row alone is
  170 ms: a thirteen-term query matches nearly the whole corpus, and `ts_rank_cd` is evaluated over
  every match before the pool cut. The BM25 row adds about 30 ms on top of that. On NFCorpus the
  selection is 4 ms at the median with a long tail, because the match set is smaller.
- **On a small corpus with long queries, OR matching is a full scan.** That is the recall it buys,
  and it is the lexical engine's cost as much as this one's.
- **The pool is a quality knob more than a time one.** Halving it to 500 costs 0.005 nDCG@10 on
  NFCorpus and 0.077 on SciFact and barely moves the median; doubling it costs 0.10 in SciFact's
  tail. 1,000 is the default for that reason.
- **This is not a speed replacement for `pg_search`.** Tantivy answers from its own inverted index
  instead of ranking the match set in PostgreSQL, which is why the extension exists. What this
  engine buys is portability; the table is its price.
- **The statistics build is the first search after a write**, not a search: `ts_stat` walks the
  table, and the column is measured on its own so it does not hide inside a p99.

The read is two-phase for this reason: the candidate query ranks ids and carries one `tsvector` per
candidate, and the page's documents are fetched afterwards, so a thousand candidates do not drag a
thousand contents and jsonb rows through the sort. The per-candidate reader keeps the query's terms
and counts the rest without allocating their strings. On NFCorpus those two changes took the
pool-1,000 row from 27 ms to 16 ms at the median and from 64 ms to 37 ms at the mean.

**What it is not.** The analysis is PostgreSQL's, not this library's tokenizer, so it matches the
in-memory rows at most and no published figure. Weights (`FieldWeights`) are a `ts_rank_cd`
concept and do not reach this ranking.

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

`ContentTokenizer` is the one option in this package that reaches DDL **verbatim**: it is
interpolated into `CREATE INDEX ... USING paradedb`, because a Tantivy tokenizer is an
expression (`pdb.simple('ascii_folding=true', 'alias=content')`) and not a name that could be
allow-listed like `Schema` or `Table`. Treat it, and `IndexName`, as trusted configuration —
the same class of value as a connection string. A deployment that builds these from user input
has put SQL in the user's hands.

`FetchLimit` caps at 100 000 rows per query in every SQL engine, so an offset past it returns a
short page rather than moving the whole table across the wire.

Scores that are exactly equal are ordered by document id, ordinally, on every SQL engine — the page
the in-memory engines return under the default `TieBreak.DocumentId`. The statements spell it
`id COLLATE "C"` because the database's default collation is locale-sensitive and `en_US.utf8` sorts
`a` before `A`. A bare `ORDER BY score DESC` leaves the order to the executor, so the same query can
page differently after a plan change. `TieBreak.InsertionOrder` has no counterpart on a SQL table
and is ignored there: the id order is the answer.

ParadeDB's scores are Tantivy's and PostgreSQL-native, so they are **not** numerically comparable
to `Bm25Scorer`/`TfIdfScorer` — use the hybrid engine's `ReciprocalRankFusionMerger` (or re-rank)
for a single cross-engine ordering. The stock-PostgreSQL BM25 engine above is the opposite: its
scores are `Bm25Scorer`'s. Note the extension is **AGPL-3 licensed**: fine for SaaS/internal use,
but review it if you distribute the stack.

## Managed PostgreSQL

What each engine needs server-side, and whether the large managed services provide it:

| requirement | RDS / Aurora | Cloud SQL | Azure Database for PostgreSQL |
|---|---|---|---|
| `unaccent`, `pg_trgm`, `fuzzystrmatch` | yes | yes | yes |
| `pgvector` (vector engine) | yes | yes | yes |
| `pgvector` ≥ 0.7.0 (sparse engine, `sparsevec` + HNSW) | yes on current majors | version-gated | yes on current majors; older servers ship 0.5.1, which has no `sparsevec` |
| `pg_search` (ParadeDB engine) | **no** | **no** | **no** |

Everything else the engines need is stock PostgreSQL: the lexical and BM25 engines run anywhere,
and the fuzzy engine needs only contrib.

Three things bite in practice:

- **`CREATE EXTENSION` is privileged.** RDS wants `rds_superuser`, Cloud SQL `cloudsqlsuperuser`,
  Azure `azure_pg_admin`. A deployment role without it fails at `EnsureSchema`, not at first query.
- **Azure and Cloud SQL gate extension installation by allow-list.** On Azure the extension must
  also be listed in the `azure.extensions` server parameter before `CREATE EXTENSION` succeeds.
  Set it in the deployment steps, not in application code.
- **`pg_search` is not installable on any of the three.** It is a preloaded library plus an index
  access method, and the managed services ship a curated extension list. The portable BM25 is
  `PostgresBm25SearchEngine`; if you want ParadeDB itself, run it beside the managed database (it
  documents logical replication from RDS, Cloud SQL, Azure, Supabase and Neon into a ParadeDB
  cluster) or use a managed ParadeDB offering.

By default the tests are skipped unless `POSTGRES_TEST_CONNECTION` points at a live instance. What
each group needs is what its engine needs: the lexical and BM25 tests run on stock PostgreSQL with
contrib (`unaccent`), the vector and sparse tests need `pgvector` (`pgvector/pgvector:pg16`), and
the ParadeDB tests need `pg_search` (`paradedb/paradedb:pg16`, which ships it preloaded) — they
self-skip when the extension is absent.
