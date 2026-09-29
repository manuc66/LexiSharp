# Changelog

All notable changes to LexiSharp are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html) with the 0.x caveat: **nothing is
frozen before 1.0.** A minor bump may break the public API, and the notes below say when it did.

Every measured figure lives with the thing that measures it, not here. A number that is only
written in this file is an assertion; the numbers below are pointers to
`bench/LexiSharp.Eval/reference/pinned.json`, which is replayed by

```bash
dotnet run --project bench/LexiSharp.Eval -c Release -- --verify-reference
```

on every push that touches the library or the harness, and by the `Pinned reference` workflow.

## [0.7.0]

### Added

- **`QueryTermWeighting`** on `Bm25Scorer`, `Bm25PlusScorer` and `Bm25LScorer`. `Distinct` is the
  default and remains so; `QueryFrequency` counts a repeated query term once per occurrence. It is
  what the two ArguAna pins `arguana/english+qtf` and `arguana/english+qtf at k1=3.0` hold, and it
  is how a query whose text is a whole document — rather than a bag of keywords — is scored.
- **`StopWords.EnglishFunction`**, the 33-word set the conventional English analysis uses, so an
  index can be built that is comparable with the published BM25 figures. `StopWords.English` is
  unchanged: every recorded baseline was taken with it, and changing it would have invalidated
  them.
- **`NdcgGain`**, and the linear convention in `RetrievalMetrics`, which is the one the published
  figures use. `Exponential` stays the default, so no number moved.
- **`SearchOptions.ExcludedDocumentIds`**, which excludes the query's own document from the
  results. It is in the library rather than the harness because the tuner builds its own
  `SearchOptions` per query, and a harness-side filter would have left the tuned rows and the
  reranker disagreeing with the rest of the table.
- **`--fingerprint`** in the evaluation harness: documents, non-empty documents and total terms
  against the published index statistics, in integers, checked before any score is computed.
- **`--verify-reference`** and `reference/pinned.json`. Eight regression pins asserted at
  ±0.002, three index fingerprints, and three parity rows kept as evidence with their source rather
  than as assertions. Regenerate with `--verify-reference --write`; read the diff before
  committing it.
- **A CI workflow that replays the pins.** It exists because three documents promised this net and
  nothing ran it.
- **`LexiSharp.CodeShape`**, build tooling that reads the structural shape of a compiled
  assembly — per method, the length of its IL and its decoded local signature — so a codegen
  change is diffable without rebuilding two commits on one machine.
- **An API reference generated from the assemblies**, checked in CI, replacing prose that had to be
  maintained by hand.

### Changed

- **Scoring is term-at-a-time.** `SearchQueryPlan` folds the inverted lists into an ordinal-indexed
  buffer, so a query costs one walk per term instead of two string-dictionary lookups per
  (document, term) pair. `BM25`, `BM25+`, `BM25L` and `TF-IDF` qualify; `BM25F` and query
  likelihood do not, because their contribution is not per-term, and they keep the per-document
  loop. The two loops produce the same doubles, not merely the same ranking, which matters because
  ties are broken on the score. The per-candidate figures are in
  [docs/benchmarks.md](docs/benchmarks.md).
- **The package version is declared once**, in `Directory.Build.props`, instead of in each of the
  four project files. `VersionTests` holds it there.
- **CS1591 is no longer suppressed.** Every public member now has a `<summary>`, and the generated
  reference reads the same XML, so a hole shows up there as an empty cell.
- **The golden master is a gate, not public API.** It is how ranking output is pinned; the type is
  no longer part of the surface a consumer depends on.

### Fixed

- **A repeated query term now reaches the scorer through the engine.** `QueryTermWeighting` was
  inert on the main search path: `RankedTextSearchEngine` deduplicated the query before handing it
  to the scorer, and the scorer short-circuits on a list it can prove is already distinct, so the
  two settings produced bit-identical rankings on every metric. Pinned by `arguana/english+qtf` and
  `arguana/english+qtf at k1=3.0`, and covered by `QueryTermWeightingTests`. The engine keeps its
  deduplicated list for the two decisions that need it — `SumDocumentFrequencies` and candidate
  enumeration — because a query repeating a term thirty times would otherwise claim thirty times
  the reach and flip the scoring path for a query matching the same documents.
- **The filter loop allocated an enumerator per candidate document.**
- **The drift message pointed at a flag that writes nothing.**

### Withdrawn

- **A quality claim that no code path could produce.** The README and two documentation pages
  reported a figure on ArguAna that the deduplicating engine could not have returned, and
  attributed the gap to a scoring rule that measurement then priced differently. The claim and the
  setting it rested on both landed on `main` **after** the `v0.6.0` tag — `QueryTermWeighting` is
  absent from it, and `git tag --contains` returns no tag for the commit that added it — so no
  published release ever carried either. The measurement, the two conventions that were checked and
  ruled out, and the structural reason are in
  [docs/evaluation.md](docs/evaluation.md#measured-against-published-baselines); the figures are the
  `parity` rows of `reference/pinned.json`.
- **Three documentation pages led with a comparison that was not like-for-like.** They reported
  this library's defaults against figures produced by a different analysis, at different parameters,
  with a different gain convention and a different rule for repeated terms. Both readings are now
  kept and labelled: the defaults answer *what the library does*, the aligned run answers *is this
  comparable*.
- **A benchmark intercept.** Three "fixed cost" figures in `docs/benchmarks.md` were read as an
  intercept; part of that cost is an `Array.Clear` of the whole ordinal space, and the measurements
  behind the table are not affine in document frequency, which is what an intercept would require.
  The two slopes were re-measured at 100,000 documents; the intercepts were not re-run and the
  correction says so.

## [0.6.0] and earlier

Not recorded here. The tags are the record: `git log v0.6.0..main` for what changed, and the
commit messages carry the measurements.
