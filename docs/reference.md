---
title: Reference
nav_order: 13
description: >-
  The packages, the architecture, the scoring conventions, the scope and the limits,
  and how to build and test LexiSharp.
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

### Native AOT

`LexiSharp` and `LexiSharp.Postgres` carry `IsAotCompatible`, so the trim/AOT analyzers
(`IL2026`, `IL3050`) are build errors. CI then publishes
[`tests/LexiSharp.AotSmoke`](https://github.com/manuc66/LexiSharp/tree/main/tests/LexiSharp.AotSmoke)
with `PublishAot` — ILC over the whole closure — and runs the binary: index, search, and a
document round trip through PostgreSQL. A regression fails the workflow on every push to `main`
and every pull request.

```bash
dotnet publish tests/LexiSharp.AotSmoke -c Release -r linux-x64 -o /tmp/aot-smoke
/tmp/aot-smoke/LexiSharp.AotSmoke
```

Without `POSTGRES_TEST_CONNECTION` the Postgres half reports itself skipped; ILC compiles it
either way, because the code is rooted whether or not the variable is set.

**Not AOT-validated, and not claimed to be: `LexiSharp.MessagePack` and `LexiSharp.AspNetCore`.**
MessagePack's resolver reaches for reflection on first use, so `Save` fails in an AOT binary
(`MissingMethodException` on `ListFormatter<T>`); the AspNetCore endpoint's
`MapGet(route, Delegate)` is annotated `RequiresUnreferencedCode`/`RequiresDynamicCode` and stops
the build.

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
- **One scorer departs from its published formula to keep that convention.** Read literally,
  BM25+ assigns `idf(t)·δ` to every query term, including terms the document does not contain, so
  the paper's model scores non-matching documents *above* zero. Reference implementations subtract
  a non-occurrence term to compensate. This implementation instead sums only over terms the document
  actually has, which keeps it an `ITermOverlapScorer` and is the reason its scorer-level agreement
  with the paper is conditional. BM25L needs no such accommodation — its term weight is already `0`
  at `tf = 0`. The arithmetic and the departure are in
  [BM25 variants](ranking.md#bm25-variants).
- Every sub-system is culture-agnostic; text is normalized to lowercase without accents
  so that `"Résumé"` and `"resume"` match.
- Scores are **not comparable across engines** unless you merge them by rank. A PostgreSQL
  `ts_rank_cd`, a ParadeDB BM25 score, a BM25 score and a cosine are four different scales;
  [RRF](pipelines.md#hybrid-engine) exists precisely because of that.

## Scope and limits

- **Version 0.6.0, single maintainer.** The library is young and its public API may still
  change between minor versions — pin a version and read the release notes if you adopt it
  early. Contributions and feedback are welcome.
- **Built on established IR.** The techniques implemented (BM25, RRF, SPLADE-style sparse
  retrieval, MaxSim) follow well-documented information-retrieval literature; the value here
  is a small, dependency-free .NET implementation of them, not new research.
- **Performance numbers are indicative.** They live in [Benchmarks](benchmarks.md) and were
  measured on one machine — always measure on your own corpus.
- **Every "tuned" figure in this documentation is an in-sample upper bound.** All four tuners
  fit and score on the same queries, so the more configurations were tried the more of the reported
  gain is fitting noise — 125 points fit more than 25. Score a winner on a held-out set before
  trusting its margin, and check that the objective is not flat: a tuner pointed at a flat metric
  has no signal to fit, and two figures in this repository's history were wrong for exactly that
  reason. See [Tuning](evaluation.md#tuning-bm25-bm25f-bm25-and-bm25l).
- **No scorer here has a measured win over a tuned BM25.** The three that could plausibly have one
  came back negative or null, and publishing that is the point:
  - *BM25+ and BM25L*, tuned on their own `δ`, **tie** a tuned BM25 on the reference corpus and
    NFCorpus, and edge it by 0.002–0.004 on SciFact — an in-sample margin, so an upper bound rather
    than a result. On ArguAna, at a fixed `δ`, they **lose** 0.040–0.046, and no `δ`-tuned ArguAna
    row exists, so whether tuning closes that gap is **unmeasured**. See
    [BM25 variants](ranking.md#bm25-variants).
  - *BM25F*'s tuner reports `WeightingHelped = false` on every corpus tried, and on the reference
    corpus — re-fitted on the metric being reported — BM25F tuned trails BM25 tuned by 0.011. See
    [BM25F](ranking.md#bm25f-field-weighted-bm25).
  - *Proximity* is neutral at quarter strength and negative at full strength. See
    [Proximity](ranking.md#proximity).
- **Corpus-derived expansion is a measured loss** where it has been measured (0.8469 against BM25's
  0.8751 on the reference corpus) and is not a default for that reason.
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

The [API reference](api.md) is generated from those assemblies and the XML documentation they
ship, so it cannot describe a surface that does not exist. A new public type with no regenerated
page fails the build, which is the moment a type is still cheap to document:

```bash
dotnet run --project bench/LexiSharp.ApiDocs -c Release -- write   # regenerate docs/api.md
dotnet run --project bench/LexiSharp.ApiDocs -c Release -- check   # what CI runs; exit 1 if stale
```

Its README is at
[`bench/LexiSharp.ApiDocs`](https://github.com/manuc66/LexiSharp/blob/main/bench/LexiSharp.ApiDocs/README.md)
— an absolute link, because a relative one resolves on GitHub and 404s on the published site,
which serves this folder from `/LexiSharp/`. It is worth reading before editing a row: the
*What it is* column is the type's own XML summary, so an edit that disagrees with the source is
overwritten on the next run.

Postgres/ParadeDB integration tests run against whatever `POSTGRES_TEST_CONNECTION` points to:
`pgvector/pgvector:pg16` covers the lexical + vector + sparse + fuzzy suites,
`paradedb/paradedb:pg16` covers the lexical + ParadeDB (BM25) + fuzzy suites. The sparse tests
self-skip when the `vector` extension is unavailable. The fuzzy tests
self-skip when `pg_trgm` (and `fuzzystrmatch`, when exercised) are unavailable.

## License

MIT — see [LICENSE](https://github.com/manuc66/LexiSharp/blob/main/LICENSE). The ParadeDB
`pg_search` extension used by the BM25 backend is licensed separately, under AGPL-3.
