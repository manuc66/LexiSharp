---
title: Reference
permalink: pretty
nav_order: 13
---

# Reference

The contracts, the package layout, the conventions every engine shares, and how to build,
test and reproduce the numbers.

## Packages

| Package | Responsibilities |
|---|---|
| `LexiSharp` | records + interfaces + in-memory index + scorers + tokenizer + `IEmbeddingProvider` + `ISparseEmbeddingProvider` + sparse engine + in-memory dense engine + hashing embedding provider (export/import) + boost/rerank decorators + filters + highlighting + facets + similarity + keywords + metrics + hybrid federation (RRF, weighted, cascade, cross-encoder, MMR, MaxSim) |
| `LexiSharp.Postgres` | PostgreSQL providers: `tsvector`+`unaccent` (lexical), `pgvector` ANN (vector), `pgvector sparsevec` (sparse), `pg_trgm`+`fuzzystrmatch` (fuzzy), `pg_search`/Tantivy (true BM25) |
| `LexiSharp.MessagePack` | MessagePack (binary) persistence for the in-memory index and the sparse engine |
| `LexiSharp.AspNetCore` | a minimal-API `GET /search` endpoint over `LexiSharpIndex<T>` |

All four target `net10.0` and are on [nuget.org](https://www.nuget.org/packages/LexiSharp/).
The core package has **no** dependency; the others bring their own (MessagePack, Npgsql).

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

## Scoring conventions

- A score of exactly `0` means *not a match* and the document is excluded from results
  (all built-in scorers honor this).
- Every sub-system is culture-agnostic; text is normalized to lowercase without accents
  so that `"Résumé"` and `"resume"` match.
- Scores are **not comparable across engines** unless you merge them by rank. A PostgreSQL
  `ts_rank_cd`, a ParadeDB BM25 score, a BM25 score and a cosine are four different scales;
  [RRF](pipelines.md#hybrid-engine) exists precisely because of that.

## Scope and limits

- **Version 0.4.0, single maintainer.** The library is young and its public API may still
  change between minor versions — pin a version and read the release notes if you adopt it
  early. Contributions and feedback are welcome.
- **Built on established IR.** The techniques implemented (BM25, RRF, SPLADE-style sparse
  retrieval, MaxSim) follow well-documented information-retrieval literature; the value here
  is a small, dependency-free .NET implementation of them, not new research.
- **Performance numbers are indicative.** They live in [Benchmarks](benchmarks.md) and were
  measured on one machine — always measure on your own corpus.
- **Combinatorial coverage is partial.** Engines, scorers, rerankers and mergers are tested
  individually *and* in the combinations described here, but not every pairing is exercised.
  Treat an unusual combination as supported but unproven until you test it on your data.
- **`SearchTrace`'s time cost is unmeasured.** Its allocation is pinned by BenchmarkDotNet
  and stable; its timing is not quoted because the baseline itself varied 2.3× between
  identical runs on the machine used. Read it as unknown, not as free — see
  [Ranking trace](observability.md#ranking-trace-searchtrace).
- **No OpenTelemetry or Prometheus exporter.** `IRetrievalMetrics` is the only number
  surface; implement it over your own backend for a real metrics pipeline. See
  [Observability](observability.md#observability-retrievaltelemetry).

## Building & testing

```bash
dotnet build LexiSharp.slnx
dotnet test  tests/LexiSharp.Tests                # xUnit suite (Postgres tests need POSTGRES_TEST_CONNECTION)
dotnet run  --project bench/LexiSharp.Benchmarks  # BenchmarkDotNet suite (published numbers: benchmarks.md)
```

Postgres/ParadeDB integration tests run against whatever `POSTGRES_TEST_CONNECTION` points to:
`pgvector/pgvector:pg16` covers the lexical + vector + sparse + fuzzy suites,
`paradedb/paradedb:pg16` covers the lexical + ParadeDB (BM25) + fuzzy suites. The sparse tests
self-skip when the `vector` extension is unavailable. The fuzzy tests
self-skip when `pg_trgm` (and `fuzzystrmatch`, when exercised) are unavailable.

## License

MIT — see [LICENSE](https://github.com/manuc66/LexiSharp/blob/main/LICENSE). The ParadeDB
`pg_search` extension used by the BM25 backend is licensed separately, under AGPL-3.
