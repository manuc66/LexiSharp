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
| `LexiSharp.Cli` | a .NET tool — `lexisharp search`, `segment build|search|info`, `benchmark`, `diff`, `baseline`, `verify` |

The four library packages target `net10.0` and are on [nuget.org](https://www.nuget.org/packages/LexiSharp/).
`LexiSharp.Cli` is a tool rather than a library: it packs the core assembly inside itself, so
`dotnet tool install -g LexiSharp.Cli` has no other package to resolve.
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

- **Version 0.8.0, single maintainer.** The library is young and its public API may still
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
- **No scorer here has a general measured win over a tuned BM25.** Two of the three that could
  plausibly have one came back null, and the third wins on one corpus and loses on another —
  publishing that is the point. The learned-sparse row reads above BM25 on three corpora, and that
  margin belongs to the external model: the library stores and scores vectors it is handed and
  implements no part of SPLADE. See
  [Learned sparse](evaluation.md#learned-sparse-splade-through-onnx).
  - *BM25+ and BM25L*, tuned on their own `δ`, **tie** a tuned BM25 on the reference corpus and
    NFCorpus, and edge it by 0.002–0.004 on SciFact — an in-sample margin, so an upper bound rather
    than a result. On ArguAna, at a fixed `δ`, they **lose** 0.040–0.046; tuned there, both pick
    `δ = 0` and read the tuned BM25's 0.363, so the gap closes by removing the bound rather than by
    finding a better one. See [BM25 variants](ranking.md#bm25-variants).
  - *BM25F*'s tuner reports `WeightingHelped = false` on the full query set of every corpus it was
    fitted on, so the field weight is never what moves those margins (a held-out half reports `true`
    by at most 0.002; see [Evaluation](evaluation.md#held-out-measurement---heldout)). Against BM25
    tuned it **wins on two corpora and loses on two**: 0.397 against 0.363 on ArguAna, 0.668 against
    0.664 on SciFact, 0.309 against 0.311 on NFCorpus, 0.8867 against 0.8978 (nDCG@5) on the
    reference corpus — with the title weight left at 1.0 in all four. Every one of those is fitted
    in-sample. See
    [BM25F](ranking.md#bm25f-field-weighted-bm25).
  - *Proximity* is neutral at quarter strength and negative at full strength. See
    [Proximity](ranking.md#proximity).
- **Corpus-derived expansion is a measured loss** where it has been measured (0.8469 against BM25's
  0.8751 on the reference corpus) and is not a default for that reason.
- **The learned and fitted channels have no measured value here.** `FeedbackAwareTextSearchEngine`
  and the contextual boost add a bonus whose worth depends on the corpus and on the history they are
  fed; no nDCG figure is claimed for either. `CalibratedScoreConfidence` fits a score → probability
  mapping and derives an abstention threshold from it, and no fitted J is reported for it on
  labelled data. For the feedback channel the open question is not only whether it pays but from how
  many recorded associations it starts to. Measure on your own data before relying on either —
  [learning from past choices](ranking.md#learning-from-past-choices-queryfeedbackhistory),
  [fitted confidence](observability.md#fitted-confidence-and-the-abstention-threshold-calibratedscoreconfidence).
- **The question trees are greedy and uncalibrated, and the linear model is the accuracy leader.**
  `DecisionTreeClassifier`, `RandomForestClassifier` and `CorpusDivider` learn binary or threshold
  feature questions; each split is the best greedy one, not the optimal tree (finding the smallest
  separating tree is NP-hard), and a leaf's distribution is the smoothed label mix of the documents
  that reached it, not a probability of correctness. A numeric feature is learned as an inequality
  (`hour >= 14`), so adjacent values generalize; a categorical bucket, a transition and a process
  hierarchy are still flat names. A feature can be present, absent or unknown; a binary question
  answers no for unknown, `UnknownIndicatorSource` makes the unknown explicit for the binary
  models, and `MaskedFeatureSource` is the caller-side seam for excluding a dimension when an item
  is generic or excluded. On the standard `20news-bydate` split the single
  tree reads **0.4457** accuracy / **0.4945** macro-F1, the forest **0.6621** / **0.6584**, Naive
  Bayes **0.7573** / **0.7289**, and `LinearClassifier` **0.8085** / **0.8027**: the tree and forest
  are explainability/ensemble options and the linear model is the accuracy one. Every figure
  carries a percentile-bootstrap 95 % interval over the test split, and a stratified 5-fold
  cross-validation on the train split reads **0.8918 ± 0.0070** for the linear model and
  **0.8190 ± 0.0063** for Naive Bayes — about eight points above the held-out figures, because
  `20news-bydate`'s halves are disjoint date windows. The held-out number is the one to quote;
  the CV table is published with that limit. A temperature
  calibrator exists (`TemperatureCalibrator` / `CalibratedClassifier`); on 20NG the linear model
  reads **T = 1.04**, log-loss **0.7741 → 0.7650**, Brier **0.2855 → 0.2855**, so its softmax was
  already close. The forest's averaged probabilities are underconfident (**T = 0.35**, log-loss
  **1.8156 → 1.2785**, Brier **0.6713 → 0.4701**), while the tree's **T = 0.87** does not transfer
  and its test loss is slightly worse. The forest is a bagged ensemble with a global feature
  subset per tree, not Breiman's per-split sampling; it has no OOB estimate or feature importance.
  `IdfWeighting` is available and measured a loss on 20NG (**0.7753 / 0.7682** against
  **0.8085 / 0.8027**), and sublinear TF-IDF (`emitTermFrequency: true` plus `IdfWeighting`) lost
  too (**0.7757 / 0.7676**), so both are off by default. `Learn`/`Unlearn` fold one labelled
  document into a trained tree or linear model and remove it exactly, without rebuilding the
  structure or the coefficients; `--online` measures the cost (0.43 s / 0.63 s for 2 260 documents
  against 14.1 s / 25.7 s for a full retrain in the same run). `ClassificationModelSerializer`
  saves and loads a trained tree, forest, linear model or calibrated wrapper in a versioned binary
  (`LXCL`, version 1) that carries the vocabulary, the prediction options and the ledger; the
  tokenizer is not stored, so load with the same feature source. The long-document figures do not
  transfer to short text: with only the `Subject`
  line the linear model reads **0.5533 / 0.5295** and Naive Bayes **0.5624 / 0.5293**, with no
  significant difference (McNemar p = 0.16), so run the baseline on your own short texts first.
  The command and the pin are in
  [Evaluation](evaluation.md#classification-on-20-newsgroups); [Benchmarks](benchmarks.md#classification-classificationbenchmarks)
  reports cost. Measure on your own labels before trusting either. See
  [question trees](text-analysis.md#question-trees-classification-and-corpus-division).
- **Combinatorial coverage is partial.** Engines, scorers, rerankers and mergers are tested
  individually *and* in the combinations described here, but not every pairing is exercised.
  Treat an unusual combination as supported but unproven until you test it on your data.
- **`SearchTrace`'s time cost is unmeasured.** Its allocation is pinned by BenchmarkDotNet
  and stable; its timing is not quoted because the baseline itself varied 2.3× between
  identical runs on the machine used. Read it as unknown, not as free — see
  [Ranking trace](observability.md#ranking-trace-searchtrace).
- **In-box instruments and spans, no exporter.** `RetrievalDiagnostics` implements
  `IRetrievalMetrics` over a `System.Diagnostics.Metrics.Meter` named `LexiSharp` and exposes an
  `ActivitySource` named `LexiSharp.Search`, using types that are in-box on .NET 10 — the core
  package references no package for it. It ships no exporter, aggregator or dashboard, and no
  OpenTelemetry SDK dependency: a host's SDK, a `MeterListener` or an `ActivityListener` attaches
  the backend. Engine and stage tags are caller-supplied and must stay low-cardinality; query text
  and document ids are not tags. See
  [Observability](observability.md#in-box-instruments-and-spans-retrievaldiagnostics).

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

Postgres/ParadeDB integration tests run against whatever `POSTGRES_TEST_CONNECTION` points to.
`paradedb/paradedb:pg16` packages `pgvector`, `pg_trgm` and `pg_search`, so it runs the **whole**
suite with nothing skipped — verified on a fresh database, where the pgvector image still skips
the ParadeDB lane because `pg_search` is absent. The sparse tests install the `vector` extension
they need when the image packages it, rather than skipping a fresh database that has not had it
created yet; a server without pgvector still skips them, as does a server without `pg_trgm`
(and `fuzzystrmatch`, when exercised) for the fuzzy tests.

## License

MIT — see [LICENSE](https://github.com/manuc66/LexiSharp/blob/main/LICENSE). The ParadeDB
`pg_search` extension used by the BM25 backend is licensed separately, under AGPL-3.
