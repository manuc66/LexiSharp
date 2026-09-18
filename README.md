# LexiSharp

> A lightweight lexical text search and classification library for .NET.

LexiSharp provides a small, dependency-free set of interfaces and implementations for
indexing plain text and retrieving/ranking/classifying documents **without any semantic
or ML model** — pure lexical statistics.

## Features

- **Pluggable architecture**: an `ITextIndex`, `ITextScorer` and `ITokenizer` are
  independent contracts; algortihms can be swapped without touching the engine.
- **Four ranking strategies** behind the same `ITextSearchEngine`:
  - `Bm25Scorer` — Okapi BM25 (generally the best classical choice), with ready-made
    `Bm25Parameters` profiles (`Balanced`, `Aggressive`, `Conservative`),
  - `TfIdfScorer` — TF-IDF,
  - `QueryLikelihoodScorer` — probabilistic language model (Jelinek-Mercer smoothing),
  - `BooleanScorer` — exact AND/OR filter.
- **In-memory inverted index** (`InMemoryTextIndex`) with term positions, document
  frequencies, corpus statistics and incremental `Add`/`Remove`, plus an index statistics
  snapshot (`GetStatistics`: documents, vocabulary, tokens, average length, vocabulary richness).
- **Score boosting** (`BoostedTextSearchEngine`): a decorator that applies **signed** score
  adjustments (multiplicative factor and/or additive offset) per result — boost a category or a
  priority, damp or penalize stale matches — without touching the underlying engine.
- **Explainable scoring**: `Bm25Scorer` implements `IScoreExplainer`, and
  `RankedTextSearchEngine.Explain` returns a per-term breakdown (TF, IDF, term score, length
  normalization, parameter values) of any ranking decision.
- **BM25 tuning**: `Bm25ParameterTuner` grid-searches `k1`/`b` against your own validation
  queries, judged by `Precision@k`, `Recall@k`, `F1@k` or `nDCG@k` (`RetrievalMetrics`).
- **Supervised classification** (`NaiveBayesClassifier`): multinomial Naive Bayes with
  Laplace smoothing, exposing a dedicated `ITextClassifier` interface.
- **Configurable tokenizer**: Unicode NFKD normalization and diacritics removal,
  lowercasing, optional stop-word removal, optional n-grams, and a pluggable
  `IStemmer` seam (no stemmer is bundled on purpose — bring your own, e.g. Snowball).
  Tokenization is SIMD-accelerated (`SearchValues` + `IndexOfAnyExcept`, with a
  `System.Text.Ascii` fast path in normalization).
- **Optional backends**, shipped as separate packages:
  - `LexiSharp.Postgres` — PostgreSQL backends implementing the same `ITextSearchEngine`:
    a lexical engine over `tsvector` + GIN + `unaccent`, an ANN engine over `pgvector`
    (HNSW/IVFFlat) driven by an external `IEmbeddingProvider`, and an approximate
    **fuzzy** engine over the `pg_trgm` trigram extension (with optional `fuzzystrmatch`
    refinement);
  - `LexiSharp.ParadeDB` — **true Okapi BM25** on top of the `pg_search` Tantivy extension
    (AGPL-3, requires the ParadeDB Docker image or self-hosted extension);
  - `LexiSharp.Hybrid` — a federated engine that queries several engines and merges
    their results into one coherent ranking.

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

Change the ranking algorithm without rebuilding the index:

```csharp
var bm25Engine   = new RankedTextSearchEngine(index, new Bm25Scorer());
var tfIdfEngine  = new RankedTextSearchEngine(index, new TfIdfScorer());
var booleanEngine = new RankedTextSearchEngine(index, new BooleanScorer(BooleanMatch.AllTerms));
```

### Classification

```csharp
using LexiSharp.Classification;

var classifier = new NaiveBayesClassifier();
classifier.Train(trainingDocuments); // requires a non-null SearchDocument.Category

foreach (var prediction in classifier.Predict("i cannot connect to the internet"))
    Console.WriteLine($"{prediction.Category}: {prediction.Probability:P}");
```

### Tokenizer customization

```csharp
using LexiSharp.Linguistics;

var tokenizer = new Tokenizer(new TokenizerOptions
{
    RemoveStopWords = true,       // English list, or provide StopWords.Create(...)
    NGramMax = 2,                 // produce unigrams + bigrams
    Stemmer = new MyStemmer(),    // implement IStemmer (French, Snowball, ...)
});
```

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
`ITokenizer` cannot be serialized: hand the same implementation to `Load` — a type-name check
protects against rebuilding with the wrong pipeline. Stemmed tokenizers likewise require the
original tokenizer at load time (stemmers are not serializable).

### Explainable scoring and BM25 tuning

Audit any ranking decision term by term, then let the corpus pick its own parameters:

```csharp
var engine = new RankedTextSearchEngine(index, new Bm25Scorer());
ScoreExplanation? why = engine.Explain("doc-1", "search engine");
// why.Terms -> per-term TF, IDF and score contribution; why.LengthRatio, why.Parameters...

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

By default the integration tests are skipped unless `POSTGRES_TEST_CONNECTION` points at a
live instance (e.g. `Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=lexisharp`).
The vector tests additionally require the `vector` extension: use the `pgvector/pgvector:pg16`
image (lexical tests only need stock PostgreSQL).

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

### ParadeDB backend (`LexiSharp.ParadeDB`)

True Okapi BM25 ranking, computed by Tantivy inside PostgreSQL through the `pg_search`
extension — the strongest fit when *lexical ranking quality* is a real business requirement.

```csharp
// install once:  dotnet add package LexiSharp.ParadeDB
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

### Hybrid engine (`LexiSharp.Hybrid`)

Federate a **hot** in-memory index and a **cold** persistent backend, and produce one
consistent global ranking:

```csharp
// install once:  dotnet add package LexiSharp.Hybrid
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

Reciprocal Rank Fusion never looks at scores, so it is the natural bridge for the future
embedding-backed engines: cosines from any model land in the same formula without calibration.

**Embeddings are an agreed seam, not a feature here**: `IEmbeddingProvider` (core) describes how a
consumer project (ONNX model, model server, ...) would produce vectors — LexiSharp never
computes embeddings — and `VectorSimilarity` provides pure cosine math. `PostgresVectorSearchEngine`
is the reference consumer: it turns any provider into an ANN backend that the same
`HybridTextSearchEngine` merges exactly like a lexical engine.

## Architecture

```
Package            Responsibilities
─────────────────────────────────────────────────────────────────────────────
LexiSharp         records + interfaces + in-memory index + scorers + tokenizer + IEmbeddingProvider + boost decorator
LexiSharp.Postgres  PostgreSQL providers: tsvector+unaccent (lexical), pgvector ANN (vector), pg_trgm+fuzzystrmatch (fuzzy)
LexiSharp.ParadeDB   true BM25 provider on the pg_search (Tantivy) extension
LexiSharp.Hybrid    federated engine + mergers (RRF, weighted, reranking)
LexiSharp.MessagePack   MessagePack (binary) persistence for the in-memory index
```

Within the core package, separation of concerns mirrors the recommendations the library
was designed from:

- the **index** owns corpus statistics (tf, df, document length, positions, vocabulary);
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
dotnet run  --project bench/LexiSharp.Benchmarks  # BenchmarkDotNet suite
```

Postgres/ParadeDB integration tests run against whatever `POSTGRES_TEST_CONNECTION` points to:
`pgvector/pgvector:pg16` covers the lexical + vector + fuzzy suites,
`paradedb/paradedb:pg16` covers the lexical + ParadeDB (BM25) + fuzzy suites. The fuzzy tests
self-skip when `pg_trgm` (and `fuzzystrmatch`, when exercised) are unavailable.

## License

MIT