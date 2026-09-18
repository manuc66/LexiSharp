# LexiSharp

> A lightweight lexical text search and classification library for .NET.

LexiSharp provides a small, dependency-free set of interfaces and implementations for
indexing plain text and retrieving/ranking/classifying documents **without any semantic
or ML model** — pure lexical statistics.

## Features

- **Pluggable architecture**: an `ITextIndex`, `ITextScorer` and `ITokenizer` are
  independent contracts; algortihms can be swapped without touching the engine.
- **Four ranking strategies** behind the same `ITextSearchEngine`:
  - `Bm25Scorer` — Okapi BM25 (generally the best classical choice),
  - `TfIdfScorer` — TF-IDF,
  - `QueryLikelihoodScorer` — probabilistic language model (Jelinek-Mercer smoothing),
  - `BooleanScorer` — exact AND/OR filter.
- **In-memory inverted index** (`InMemoryTextIndex`) with term positions, document
  frequencies, corpus statistics and incremental `Add`/`Remove`.
- **Supervised classification** (`NaiveBayesClassifier`): multinomial Naive Bayes with
  Laplace smoothing, exposing a dedicated `ITextClassifier` interface.
- **Configurable tokenizer**: Unicode NFKD normalization and diacritics removal,
  lowercasing, optional stop-word removal, optional n-grams, and a pluggable
  `IStemmer` seam (no stemmer is bundled on purpose — bring your own, e.g. Snowball).
  Tokenization is SIMD-accelerated (`SearchValues` + `IndexOfAnyExcept`, with a
  `System.Text.Ascii` fast path in normalization).
- **Optional backends**, shipped as separate packages:
  - `LexiSharp.Postgres` — a PostgreSQL full-text engine over `tsvector` + GIN +
    `unaccent`, implementing the same `ITextSearchEngine`;
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

### PostgreSQL backend (`LexiSharp.Postgres`)

Persistent, shared, concurrent full-text search on top of a classic PostgreSQL setup:

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
when you need one consistent ordering. To opt into **ANN/embeddings** later, add a
`pgvector` column and an HNSW/IVFFlat index yourself: the provider deliberately stays lexical.

By default the integration tests are skipped unless `POSTGRES_TEST_CONNECTION` points at a
live instance (e.g. `Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=lexisharp`).

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
| `ReciprocalRankFusionMerger` | `Σ 1/(k + rank)` (k=60), rank-only | engines with **incomparable scales** — lexical + vector, ts_rank_cd vs BM25 |
| `WeightedScoreResultMerger` | normalized per-engine score blend | native scores trusted, per-engine weights wanted |

Reciprocal Rank Fusion never looks at scores, so it is the natural bridge for the future
embedding-backed engines: cosines from any model land in the same formula without calibration.

**Embeddings are an agreed seam, not a feature here**: `IEmbeddingProvider` describes how a
consumer project (ONNX model, model server, ...) would produce vectors — LexiSharp never
computes embeddings — and `VectorSimilarity` provides pure cosine math. A future
embedding-backed engine (in-memory HNSW, PostgreSQL `pgvector` ANN) can be fed to the same
`HybridTextSearchEngine` and merged exactly like a lexical engine.

## Architecture

```
Package            Responsibilities
─────────────────────────────────────────────────────────────────────────────
LexiSharp         records + interfaces + in-memory index + scorers + tokenizer
LexiSharp.Postgres  PostgreSQL provider (tsvector + GIN + unaccent)
LexiSharp.Hybrid    federated engine, mergers, IEmbeddingProvider seam
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

## License

MIT