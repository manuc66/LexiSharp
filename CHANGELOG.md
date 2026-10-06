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

on every push that touches the library or the harness, and weekly by the `Pinned reference`
workflow.

## [Unreleased]

### Added

- **A learned query → document channel, and a fitted confidence with an abstention threshold.**

  `QueryFeedbackHistory` records which document each query resolved to. `FeedbackAwareTextSearchEngine`
  consults it on every search and adds the association's strength to the primary score, bounded by
  `maxBoost`. The bonus is applied after the inner engine has ranked, so it promotes among what the
  base ranking returned and never introduces a document it rejected — it composes with a boosted or
  hybrid engine instead of replacing one. `GetFuzzyAssociations` reaches a differently-worded query
  through term overlap; `Snapshot`/`Restore` persist the history with its multiplicities.

  `CalibratedScoreConfidence` fits a score → probability mapping on labelled `(score, isCorrect)`
  pairs, by isotonic regression (PAVA) or Platt scaling, and derives the abstention threshold from
  Youden's J over those pairs. Untrained it returns `0.5` and never abstains. This is the fitted
  counterpart to `ScoreConfidence`, which remains the unfitted, scale-invariant gauge and is
  unchanged.

- **A boost that can see the search it is adjusting, and decline.**

  `BoostedTextSearchEngine<TPayload>` takes a boost that receives a `BoostContext<TPayload>`:
  the query as issued, the inner engine's ranking **before** any boost, each candidate's
  `WinnerMargin` confidence, and the caller's payload. That is what lets a boost be conditional on
  the base ranking being uncertain — `context.TopConfidence` — which a per-candidate boost cannot
  be, since it is never shown the ranking. The context is built once per search and shared by
  every candidate, so a whole-ranking check is evaluated once per query.

  It implements `IContextualSearchEngine<TPayload>`, an optional capability alongside
  `IFacetedSearchEngine` and `IDetailedSearchEngine`. The payload type is the interface's type
  parameter, not `object`: a `BoostedTextSearchEngine<UserContext>` answers to
  `IContextualSearchEngine<UserContext>` and to nothing else, so a mismatched payload does not
  compile. It is **not** a `SearchOptions` property — that record has value equality and a
  serializable shape, and a caller-defined value among its properties would put both at the mercy
  of that type's `Equals`.

  The plain `BoostedTextSearchEngine` — candidate-only, unchanged — is now a forwarder over the
  generic one, so there is one ranking implementation.

- **`ScoreBoost.None`: the way to say "leave this candidate alone".**

  `default(ScoreBoost)` does not mean that. The optional constructor parameters give
  `new ScoreBoost()` a factor of 1, but `default` bypasses the constructor and is all zeroes, so
  its factor is 0 — which is the *exclude* case. A boost function returning `default` drops every
  document it is asked about, which is the opposite of what the expression reads as. `None` is the
  explicit value; this is called out on the type because the two read alike and mean opposites.

  Not a behaviour change for any existing caller — nothing in the repository returned
  `default(ScoreBoost)` from a boost, and a caller who did was excluding its documents already.

  No retrieval figure is claimed for either: both are new signals whose value depends on the
  corpus and on the history fed to them, and are to be measured by the caller on their data. See
  `docs/ranking.md` and `docs/observability.md`.

## [0.8.0] — 2026-10-06

### Added

- **A filtered query now uses the term-at-a-time pass by default.**
  `SearchOptions.AccumulateFilteredQueries` decides, and it now defaults to `true`; set it `false`
  for the previous behaviour. A query carrying `Filters` used to be declined by that pass and
  served by the per-document loop, which scores **every surviving candidate by document id** — one
  id-keyed length lookup plus one per query term. The pass scores by ordinal, as one walk of the
  posting entries that exist, and applies the filter to the ordinals it recorded.

  **Results do not change.** The two paths are numerically interchangeable — same terms, same
  summation order, same score bits — which the equivalence tests assert. Only the path serving the
  query moves.

  Sweeping a filter from keeping every document to keeping none, on all three indexed BEIR corpora,
  the per-document loop is **42× slower on arguana, 8.2× on scifact, 5.9× on nfcorpus** when the
  filter keeps everything, and the margin shrinks monotonically as the filter tightens. The pass
  loses only where a filter discards almost everything, and there by at most **1.7×**; on the two
  smaller corpora it wins even with nothing kept. **Not measured:** the crossover itself, and any
  corpus larger than arguana's 8,674 documents. This pass's cost is proportional to the posting
  entries a query touches rather than to what survives, so the region where it loses is expected to
  widen with corpus size — a caller whose query is broad *and* whose filter keeps almost nothing
  should set the option `false`.

  A request combining a filter with a `TieBreak` other than `DocumentId` keeps the per-document
  loop whatever the option says. The two paths do not produce candidates in the same order — a full
  scan is corpus order, the accumulation pass is posting order — so under `InsertionOrder` they
  order documents that tie exactly differently, which is the one thing that option exists to
  reproduce.

- `AtomicEngineReference`, the publish point for the « snapshot swap » pattern: build a fresh
  engine off-lock, `Swap` it in one atomic step, and readers capture the engine once per search
  through a volatile read — the frequent-writes counterpart of `SynchronizedTextSearchEngine`,
  with none of the reader-side lock cost and no claimed number attached to it. It displaces no
  engine: `Swap` returns the previous one, which the caller owns.
- `SynchronizedTextSearchEngine`, an opt-in wrapper that serializes engine writers against readers
  without serializing readers against each other — the search half of
  `SynchronizedTextClassifier`. Searches take the read lock, the mutating members the write lock,
  and `IFacetedSearchEngine`/`IDetailedSearchEngine`/`IExplainableSearchEngine` forward unchanged
  when the wrapped engine has them. The uncontended read pair is measured, not assumed: 0.984x and
  1.002x wrapped-to-unwrapped median on two same-session runs, straddling 1.0. Under write
  contention the same-measured picture is different: one writer toggling Add/Remove as fast as it
  can cut the readers' combined throughput to 0.15x the uncontended rate over a 3-second window,
  with no reader starved to zero — a ratio on one host, not a contract.
- `ExpandingTextSearchEngine` now forwards `IFacetedSearchEngine`, `IDetailedSearchEngine` and
  `IExplainableSearchEngine`, because its results are the inner engine's own against the expanded
  query — the one decorator where the forwarding is truthful. A wrapped engine without a
  capability throws by name, per call. `BoostedTextSearchEngine` and `RerankedTextSearchEngine`
  do not forward and say why in their remarks: they replace scores, and a forwarded facet page or
  explanation would describe the pre-transform ranking.
- `SearchOptions.ScoreRounding`, off by default. `ScoreRounding.FourDecimals` reproduces what a system
  that writes its scores down does to them before writing: round every returned score to four decimals,
  then walk down each run of scores within a ten-thousandth by one millionth a position, so that
  documents the arithmetic scored identically come out distinguishable. Lossy by construction and not an
  improvement — a decision about the comparison being made. Measured on ArguAna against that system's
  run: 14,168 of 14,168 compared scores identical on the raw bits, against 6.18% without it.
- `Bm25Scorer`'s `saturationConstant`, on by default. Passing `false` leaves `k1 + 1` out of the
  numerator, which is the same ranking scaled by `1 / (k1 + 1)` and therefore the same order — but the
  reference implementation's BM25 has no such factor, so a score compared against one of its runs is off
  by that factor before any rounding is discussed.
- `Bm25Arithmetic`, off by default. `SinglePrecision` computes each term's contribution in single
  precision, sums those in double and rounds the total once, and reads the length normalisation from a
  single-precision reciprocal table instead of evaluating it per document. Same ranking, one fewer
  significant figure, and it is the figure the reference has.
- Together, and only together, those three reach bit-for-bit equality with a run that reference
  implementation wrote: 14,168 of 14,168 scores, verified on all 1,406 ArguAna test queries. All three
  default to this library's own conventions and none of them is on by default.
- `LexiSharp.Eval` flags `--omit-saturation-constant`, `--single-precision-bm25` and
  `--reference-score-rounding`, and states the rounding on the run file it writes, since a score read
  back from that file is not the scorer's score when the flag is on.
- `IAccumulatingQueryPlan.Finalise`, a default-implemented member, so a scorer that rounds a score at
  the end of the sum can do so on the path that accumulates postings. It had nowhere to do it before:
  on a corpus large enough to take that path, the per-document loop is never called and the rounding
  was silently skipped.
- `IdealDcg` no longer lets a document of relevance level 0 occupy a rank in the ideal ranking. It
  added nothing to the ideal and advanced the rank, which shrank the denominator and so raised every
  nDCG computed against it. The corpus loader here drops judgments of 0, so no reported figure moves;
  it was reachable through the public `RetrievalMetrics.NdcgAtK` overload, which takes whatever map
  it is handed. Two tests cover it, one of which fails on the previous code.
- The evaluation harness measures its metrics at rank 10 rather than at the depth the run retrieves.
  The two were the same value, so a parity run — which retrieves one document deeper than it measures,
  to compare scores document by document — reported nDCG@11 under a heading reading nDCG@10. On
  ArguAna that was worth 0.006, and it is the whole of the gap this repository had been recording
  between its figure and the published one. The cutoff is now named (`MetricDepth`) and is
  independent of `--top-k`.
- `pinned.json` records the ArguAna parity figure as reproduced, at 0.397, with the verification
  written down: `trec_eval` 9.0.8 reads nDCG@10 0.3970 and recall@100 0.9324 from a run this harness
  wrote, against 0.3970 and 0.9324 published, so the aggregate no longer rests on this harness's own
  metric code. The 15,466 of 15,466 bit-identical raw scores are unchanged and are what makes the
  ranking the same ranking. A 0.0004 residual against `trec_eval` is recorded as measured, localized
  to the 102 of 1,406 queries that contain a score tie, and not explained.
- The reciprocal table `SinglePrecision` builds is now published through a single immutable reference
  behind a `Volatile` read and a lock, instead of two ordinary fields written in sequence. One scorer
  serves every query an engine runs and those run concurrently, and the previous shape let a thread
  publish a reference to an array another was still filling. It is the only state in that mode, which
  is why it went unnoticed until it was looked for: the symptom is a wrong score occasionally rather
  than a failure. Covered by a test that compares a shared scorer's answers under 64 threads with its
  own answers serially, on the raw bits.
- `IChunkContextEnricher` and `ContextEnrichingIndex`, the index-time context-enrichment seam: a
  document's searchable text is rewritten before it is tokenized — the caller's LLM-injected
  summary, or the model-free `FieldPrefixContextEnricher` that prefixes named `Fields` values — so
  the corpus that is scored can carry context the text does not spell out. The caller's original
  document stays the one `Documents`, `TryGetDocument` and every search result display, while
  statistics describe the enriched corpus. LexiSharp never runs a model itself; enrichment is
  synchronous (a model-backed implementation blocks, like `ICrossEncoderScorer`), and a changed id
  or a thrown enricher fails the `Add`. Reachable through `LexiSharpIndexOptions<>.ContextEnricher`.
- `IQueryTransformer` and `TransformingTextSearchEngine`, the pre-retrieval query-transformation
  seam: a raw query is rewritten into one or more variants — a rewrite, the sub-queries of a
  decomposed question, lexical variants, or a HyDE-style generated answer — each searched against
  the same inner engine and fused by best score per document, with the usual page cut applied to
  the fused ranking. Because every variant runs on one scorer's scale, the fusion is a per-document
  max rather than a rank fusion. A null/empty/blank result or a throwing transformer falls back to
  the untransformed query (the router rule), and variants are deduplicated. The model-free
  reference `ExpansionQueryTransformer` returns the query plus a widened variant — the
  `ExpandingTextSearchEngine` widening, fused instead of joined. Reachable through
  `LexiSharpIndexOptions<>.QueryTransformer`.
- `RetrievalEvaluator`, the runtime complement to `CorpusBenchmark`: it evaluates a <b>live</b>
  `ITextSearchEngine` — the engine an application actually serves, with whatever it currently
  holds — against a fixed panel of `BenchmarkQuery`s on demand, and reports the mean
  nDCG/MAP/MRR/recall/precision/F1 at a depth plus a per-query breakdown (retrieved ids, scores,
  rank of the first judged document). Calling it periodically and comparing successive results is
  the relevance side of data-drift observability. `CorpusBenchmark` builds its own fresh index
  from a document snapshot and compares configurations; this one measures the engine that is
  already running. The metrics are `RetrievalMetrics`'; a query without judgments is loaded but
  excluded from the averages; the caller's options pass through except `Limit`, pinned to the
  metric depth; graded judgments keep the exponential nDCG convention by default, with
  `NdcgGain.Linear` available.
- `DocumentHierarchy` and `HierarchicalTextSearchEngine`, the structure-aware slice of
  hierarchical retrieval, scoped to the application side of the split: the parent/child forest
  is declared over the ids a caller already indexes (a chunk under a section, a section under a
  book), built from a child→parent or parent→children map, and validated at construction —
  self-parenting, a node with two parents, and cycles are each refused by name. The decorator
  forwards searching and writing unchanged, and adds the `IStructureAwareSearchEngine`
  capability: `GetAncestorIds`/`GetAncestors` walk a hit's chain from its leaf up toward the
  overview, and `GetDescendants` drills the other way in breadth-first declared order. Ancestor
  ids resolve to documents through a caller-supplied ledger; without one, `GetAncestors`
  throws rather than fabricate documents. `SearchDocument` is deliberately untouched — adding
  parent/child to the central record is a breaking change with persistence and backend blast
  radius, and this seam covers the navigation without it.
- `IKnowledgeGraphBridge` and `GraphAwareSearchEngine`, the GraphRAG seam: a consumer-backed
  contract between the engines and a knowledge graph — relation extraction (subject-verb-object
  triplets via `ExtractRelationsAsync`) and neighbourhood reads (`QuerySubGraphAsync`) are the
  application's model and store (AGE, Neo4j, ...); LexiSharp runs neither. The decorator
  forwards searching and writing unchanged and adds the `IGraphSearchEngine` surface, where
  `SearchWithGraphFacts` mines the query's entities through the bridge's own extractor (one
  vocabulary for corpus and query), asks for the sub-graph, and packages the unchanged page
  with the connected facts and community summaries. A query that mines no entities skips the
  graph; the flat `Search` never touches the bridge, so a graph outage degrades nothing by
  default. Persisting extracted triplets is the implementation's ETL, not the decorator's.
- `IContextCompressor` and `CompressingTextSearchEngine`, the token-reduction seam between a
  ranked page and a generation model: `SearchCompressed` returns the same ranking with each
  hit's text reduced to its query-relevant content, and `CompressionRatio` measured once
  (`compressed.Length / source.Length`, 0 when the source is empty). The model-free
  `QueryWindowCompressor` keeps the word windows around query-term occurrences (merged,
  ellipsis-joined); a learned compressor (perplexity, LLM rewrite) is a consumer implementation
  of the same seam. The flat `Search` still returns full text — compression is an opt-in
  second surface, not a mutation of `SearchResult.Document`.
- The demo runs over a real corpus and names its analysis. `samples/LexiSharp.Demo` takes
  `--corpus` (`nfcorpus`, `scifact`, `arguana`, read from the evaluation harness's data
  directory — the demo does not download it, and the message says which command would) and
  `--segmentation` (`uax29`, the default, or `flat`). Example queries come from the corpus's
  own `queries.jsonl`, restricted to those with a positive judgement in `qrels/test.tsv`, so
  every chip has a known relevant document. `/api/meta` reports the active corpus, the
  document count and the segmentation, and the page shows them: a lane's score means nothing
  without the corpus and the analysis that produced it.

  Two defects the option carried are fixed. A hit card's title was read from
  `SearchDocument.Fields`, while a BEIR corpus carries its title as a `TextFields` entry — the
  shape `Evaluation.BuildDocuments` builds and the shape the index reads for field-aware
  scoring — so every card on a corpus with titles was titled with its document id. And
  `flat`'s description said `1,000` indexes as `1` and `000`; the tokenizer discards terms
  shorter than two characters unless `KeepSingleCharTerms` is set, which the demo does not
  set, so the term is `000` and the `1` is absent rather than present-and-light.

  The demo builds BEIR documents the way the harness does, and does not rank them the same
  way: on NFCorpus, 5 of 6 example queries come back with a different top-10 ordering under
  BM25(1.5, 0.75), for a mean top-10 overlap of 6.67 of 10, because the demo removes stop
  words and the harness's default analysis does not. Compare a demo figure against the
  harness by running the harness.

### Reproduced

**The ArguAna figure published by the independent BEIR regression — nDCG@10 0.3970 — is now
reproduced exactly, on all 1,406 queries, one by one.** Paired difference 0.00000000, 95 % interval
[0.00000000, 0.00000000]. Recall@100 (0.9324), recall@1000 (0.9872), nDCG@5 (0.3445), MAP@100 (0.3280)
and reciprocal rank (0.3282) all match as well. The two indexes agree **term for term**: 23,895 distinct terms
and 969,528 occurrences on each side, zero terms differing in document frequency, zero in term
frequency, and zero terms present in one index and not the other.

The deficit was never a ranking question. Four analysis rules accounted for it, each measured rather
than assumed, and each shipped as an option whose default is unchanged:

| option | default | what it aligns |
|---|---|---|
| `SearchOptions.ParseQuerySyntax` | `true` | a query language's `"` against prose's, 138 of 1,406 queries |
| `QueryTermWeighting.QueryFrequency` | `Distinct` | one boost per occurrence of a repeated query term |
| `TokenizerOptions.WordSegmentation` | `Flat` | 18 separators tested across digit and letter on both sides |
| `TokenizerOptions.StripPossessives` | `false` | possessive removal **before** the stop word list |
| `TokenizerOptions.FoldDiacritics` | `true` | diacritics kept |
| `InMemoryTextIndex.DocumentLengthQuantization` | `Exact` | lengths stored as one byte |

Two of these were defects in this library rather than differences of convention, and are fixed outright
below. The other four are conventions, and stay opt-in.

### Fixed

- **The sparse engine's rejection message named the pattern with a stray character in it.**
  `PostgresSparseOptions` reported that a schema and table "must match `[A-Za-z0-9_]_`", which is not
  the pattern any of the five engines checks and reads as a typo to whoever pastes the message into a
  search.

- **A schema, table or configuration name ending in a newline was accepted by the PostgreSQL
  engines.** The identifier check's pattern was `^[A-Za-z0-9_]+$`, and in .NET `$` matches at the end
  of a string *or immediately before a trailing newline* — so `"lexisharp\n"` passed. It affected
  `Schema`, `Table`, `TextSearchConfig`, `ContentField` and the `EmbeddingColumns` keys of all five
  engines, each of which reports that a name « must match [A-Za-z0-9_] ». The name is quoted before
  it reaches a statement, so a rejected-then-quoted name could not have broken one out; what it
  could do was create a table whose name is not the name anyone wrote. Those names now raise the
  same `ArgumentException` as any other character outside `[A-Za-z0-9_]`, and a property test pins
  the check to exactly that character set, so an anchoring mistake fails on generated input rather
  than on review.

- **A search that matched nothing was absent from the metrics.** `RerankedTextSearchEngine` reported
  its `retrieve` stage and then returned without a `SearchCompleted`, so a query whose inner engine
  retrieved nothing left a stage row with no search beside it — and the latency of that search, which
  on a real index is a miss rather than a fast path, never reached `RecordSearch`. A search that
  answers nothing is still a search, and it is now counted with zero results, the shape
  `HybridTextSearchEngine` already produced when every lane came back empty. A request the caller
  made empty (`Limit` of zero or less) is rejected before any work and still reports nothing.

- **A concurrent snapshot could read the minimum as zero before any search had timed.** The engine
  counters installed their "nothing seen yet" sentinel and converged on it in two separate operations,
  leaving a window in which a `Snapshot()` read the sentinel back as a minimum of `0` — indistinguishable
  from a search that genuinely finished in under a microsecond, which is the common case at this
  resolution. Seeding and converging now happen in one compare-exchange loop.

- **Sub-microsecond durations were truncated, biasing a long total low.** A sample of 0.6 microseconds
  cast to `long` recorded nothing, so a thousand of them totalled zero instead of one millisecond.
  Rounded rather than truncated.

- **The pinned reference replayed query syntax the table does not use.** `ReferenceVerifier` built
  its `SearchOptions` without naming `ParseQuerySyntax`, so every pin inherited the library default —
  a `"` is a phrase delimiter — while the table each row is compared against runs literal-text
  queries. On ArguAna, 146 of 1,406 test queries carry a straight `"`, so the whole ArguAna default
  family measured ~0.030 nDCG below the table's own figures while the gate stayed green: the pin
  family was being replayed by a path the table does not take. The four ArguAna pins are re-recorded
  to the table's convention with the syntax named in their configuration (`"querySyntax": false`):
  `arguana/default` 0.289 → 0.3195, `arguana/english+qtf` 0.271 → 0.3010, `arguana/english+qtf
  at k1=3.0` 0.331 → 0.3674, `arguana/english+exclude` 0.3806 → 0.4180. The parity figure 0.3970 is
  unaffected — it is measured on a run the table itself wrote under the same convention — and
  NFCorpus/SciFact pins are unchanged, their queries carrying too few quotes for the syntax to matter.

- **BM25's inverse document frequency was built over the wrong document count.** It used every indexed
  document; it should use the documents carrying the field. The difference is one document on any corpus
  holding one with no term in it — 1 of 8,674 on BEIR ArguAna — and it moved every score by about three
  parts in a hundred thousand, which is invisible in a ranking and decided the last digit of a score.
  Measured against a run produced by the reference implementation's own searcher: the median relative
  gap falls from 2.6e-05 to 3.3e-07. `ITextIndex.StatisticDocumentCount` is the new member, and the
  count it returns is the one `AverageDocumentLength` already divided by, so the two cannot drift apart;
  under the default divisor it is `Count` and nothing changes.

- **A combining mark ended the word it followed.** `celi\u0307l` was split into `celi` and `l` into two
  tokens, because the scanner accepted letters and digits and nothing else. A mark modifies the character
  it follows, so it extends a word and cannot open one — measured: `a\u0307b` and `a\u0307` are each one
  token, while `\u0307ab` is just `ab` and a lone mark is nothing. ArguAna holds no combining mark at all,
  SciFact none either, NFCorpus four characters out of 5,779,318; the rule is here because a word is the
  unit everything downstream counts.

- **`PorterStemmer` returned every term containing a non-ASCII character unchanged.** The guard read
  `!Ascii.IsValid(term)` and bailed, so `they’re`, `naïve`, `façade` and `exposé` were never stemmed —
  the algorithm is defined over characters, not scripts, and everything outside `aeiou` is a consonant.
  Asking the reference implementation's own stemmer settled what it does with those words (`they’re` →
  `they’r`, `naïve` → `naïv`, `façade` → `façad`); its answers are now this library's.

- **`Tokenizer` did not remove a possessive before consulting the stop word list.** Trimming the
  possessive inside the stemmer is too late: the list has already been tested, and it was tested on
  `it's` rather than on `it`. So `it's` survived as a stop word it should never have been and only then
  became `it`. On BEIR ArguAna this indexed an `it` 3,501 times where the reference has 3,134, and
  indexed a `that` (83 occurrences) and a `there` (36) the reference does not have at all — 486 of the
  487 occurrences by which the two indexes differed. The reference's own `it` comes from `its`, which
  is not a possessive and is not on the list. Correcting it in the tokenizer, before the list is
  consulted, accounts for all three to the occurrence.

- **`TokenizerOptions.WordSegmentation.UnicodeWordBoundaries` dropped a leading unconditional joiner.**
  The joiner rule needs a word character on both sides, which an underscore at the start of a token
  cannot satisfy, so `_630888` indexed as `630888` and `__alpha` as `_alpha`. Measured: the reference
  keeps `_630888`, `_alpha`, `__alpha` and `___` alone as `___`-with-no-word, while `-alpha` is just
  `alpha` — so this is the underscore's rule and not a rule about any separator.

- **`SearchOptions.ExcludedDocumentIds` was ignored on the term-at-a-time path.** The exclusion is
  implemented in `SearchOptions.PassesFilters`, and only the per-document loop called it; the
  accumulating pass carries its own gate list — NaN, infinity, zero, `MinimumScore` — and the
  exclusion was not in it. A query that took that path therefore ranked and returned the document it
  had been told to drop. The path is entered when the query reaches at least
  `max(MinimumPostingEntriesForAccumulation, OrdinalSpace / AccumulationCorpusDivisor)` documents,
  which is why the six tests for this option all passed: their fixture is three documents of
  identical text, so every one of them runs the other path.

  The cost was measurable and large on the corpus this matters for. When a query is a document's own
  text — ArguAna, where 1,298 of 1,406 test queries are corpus documents — the excluded document is
  the lexically closest thing to the query and takes rank 1. At k1=0.9, b=0.4 with the reference
  analysis, 1,406 queries: **0.271 → 0.364**, and the pin `arguana/english+exclude` moves from
  0.2864 to 0.3806. The seven other nDCG pins, all three index fingerprints and the golden master are
  unchanged. Two related measurements are settled in
  [docs/evaluation.md](docs/evaluation.md#measured-against-published-baselines): the option was inert
  on this path, and the gap between this library's defaults and the published baseline is four analysis
  conventions rather than anything about the corpus.

  The per-candidate cost is one null test on an already-hoisted set, the same shape as the gate
  already at the top of the other loop; **the benchmark could not resolve it**, so no timing claim is
  made here.

- **`SearchOptions.ExcludedDocumentIds` was ignored on every Postgres backend.** Four of the five —
  `PostgresTextSearchEngine`, `PostgresSparseSearchEngine`, `PostgresFuzzySearchEngine` and
  `PostgresVectorSearchEngine` — never read the option, so a caller excluding the query's own
  document got it back, ranked first. The fifth, `ParadeDBTextSearchEngine`, read it and still
  returned a short page: it fetched exactly `Options.Window` rows and then dropped the excluded ones
  from that prefix, so a caller asking for ten results received nine. The second failure is the
  quieter one, and it is the reason the fix is in a helper rather than four edits: one place decides
  how many rows to fetch and which to keep, and each engine applies it. `HybridTextSearchEngine`,
  `ExpandingTextSearchEngine`, `RerankedTextSearchEngine` and `BoostedTextSearchEngine` delegate to
  an inner engine, so they honour it transitively and are unchanged.

  The exclusion is applied in the C# loop rather than pushed into the SQL, which is what this
  package already does for `MinimumScore`, so the id list never becomes a parameter. The row count is
  raised by one per excluded id to keep the page full, saturating rather than overflowing. The
  arithmetic is unit-tested in `PostgresDocumentExclusionTests`; the wiring is integration-tested
  against a real server in `PostgresExcludedDocumentIdsTests`, and **that coverage runs in the CI jobs
  that carry `POSTGRES_TEST_CONNECTION`** — the suite self-skips without it, so a green run without
  that variable says nothing about this path either way.

- `RerankedTextSearchEngine`'s trace stage built its candidate map once per page entry, an
  O(page × candidates) re-fill that the per-stage `??=` only masked at the allocation site; it is
  built once, before the page loop, and SearchTraceTests still pins the step count.

### Changed

- **`QueryFeature` is renamed `QueryFeatures`.** The enumeration's own members are `None`,
  `Phrases` and `Expansions` — a set of flags, plural on every name but the type. The rename
  makes `QueryFeatures.Phrases` the sentence the rest of the API already reads like. It is a
  source-level break: a caller writing `QueryFeature.Phrases` or naming the type in a signature
  changes the identifier, and nothing else — the underlying type, the flag values and the
  wire representation are untouched.
- **`ITextIndex` splits into a read-only view and a write surface.** `IReadOnlyTextIndex` now
  carries the statistics and per-document lookups, and the scoring seams — `ITextScorer.Score`,
  `IScoreExplainer.Explain`, an `IQueryPlannableScorer`'s plan — receive that view, so the
  contract handed to a scorer proves it can only read. `ITextIndex` keeps
  `Index`/`Add`/`Remove`/`Clear`; `ICandidateIndex` and `IVocabularyIndex` are read capabilities
  over the read view. The per-field statistics leave `ITextIndex` entirely: `HasFieldStatistics`,
  `FieldTermFrequency`, `FieldLength`, `AverageFieldLength` and `FieldDocumentFrequency` move to a
  dedicated `IFieldStatisticsIndex` capability, detected with pattern matching like every other
  capability — the default-implemented members that threw `NotSupportedException` are gone.
  A caller that implements `ITextIndex` now implements `IReadOnlyTextIndex` and declares the four
  writes; a caller that used the per-field members on an `ITextIndex` value checks for
  `IFieldStatisticsIndex` instead.
- **`ITextSearchEngine.Remove` returns `bool`** — whether the document was present and removed — to
  match `ITextIndex.Remove` instead of discarding the answer. Engines forward the result of what
  they removed from; the SQL backends return whether a row was deleted.
- **A warning fails the build.** `TreatWarningsAsErrors` applies to every project, and the eleven of
  them compile with zero warnings in Release and in Debug, so nothing had to be fixed to turn it on.
  Nothing else was reading them: `dotnet build` fails on errors only, and the SonarCloud job that
  imports analyzer output into its own issues is skipped unless `SONAR_TOKEN` is configured, so a
  missing `<summary>` or a CA rule an SDK bump newly enables used to enter the tree unremarked.
  `BuildWarningGateTests` holds the property declared once, in `Directory.Build.props`. NU1903 — a
  dependency carrying a published advisory — is an error like any other, and fires when the advisory
  data is refreshed rather than when the source changes, which is what a vulnerability in a
  transitive package needs: no commit can be expected to notice it.

## [0.7.0]

### Added

- **`TokenizerOptions.WordJoiners`** and **`TokenizerOptions.WordSegmentation`**, which control which
  characters join a word and under which condition. `Flat` remains the default and joins nothing.
  Whether a character joins depends on what sits either side of it: a comma joins two digits and splits
  two letters, a colon does the exact opposite, a full stop and an apostrophe do both, an underscore
  joins anything. Flattening those into one set gets `1,2,3` right and `0335204279.pdf` wrong, and being
  wrong on one word out of a million still changes document frequencies, which changes every score.
  Eighteen separators were measured across digit and letter on both sides; characters outside that set
  are not implemented, because nothing has established here what they do.

- **`TokenizerOptions.StripPossessives`**, default `false`. The published algorithm removes a possessive
  as its first step, so without this `Adam's` stems to `adam'` — a word no query will ever contain. It
  lives in the tokenizer rather than the stemmer for a reason that was measured, not chosen: the stop
  word list is consulted in between, so trimming afterwards tests `it's` rather than `it` and lets
  through a term the caller asked to remove.

- **`TokenizerOptions.FoldTurkishDottedI`**, default `false`. Of the 505 code points whose lowercase
  differs from themselves over the ranges a European corpus contains, this is the single one where .NET's
  invariant casing and the reference implementation disagree: U+0130, the Turkish dotted capital I. Left
  alone it is the right answer for a Turkish index, where `İ` and `i` are different letters; folded, it is
  what a language-independent index wants. The fold is the Unicode **simple** mapping — U+0069, one
  character — and not the full one, which the reference's own index settles: it holds a five-character
  `celil`, and handing its analyzer a six-character `celi\u0307l` returns the dot intact.

- **`TokenizerOptions.FoldDiacritics`**, default `true`, exposed rather than fixed. Folding merges
  spellings and raises document frequencies; on BEIR ArguAna it is worth about 0.003 nDCG@10 in one
  direction, which is why an analysis claiming to reproduce a published figure has to say which way it
  went.

- **`InMemoryTextIndex.AverageLengthDivisor`** and **`DocumentLengthQuantization`**, default
  `AllDocuments` and `Exact`. The first chooses whether an empty document counts towards the average
  document length — on ArguAna, one document in 8,674, worth about one part in ten thousand, far too
  small to move a ranking but large enough to change the last bits of a score, and an exact tie is
  decided on exactly those bits. The second stores a length as one byte through an arithmetic encoding,
  which makes two documents of 149 and 151 terms score identically. Quantizing measured **worse**, by
  about 0.006: it is a device for reproducing a published figure, not an improvement.

- **`SearchOptions.TieBreak`**, default `TieBreak.DocumentId`. Two documents with the same score have
  no ranking between them, so something decides, and each convention answers a different question.
  `InsertionOrder` orders them by the position the engine produced them in, which is what a system that
  breaks ties as documents are added does — use it to reproduce one. The order stays total and
  deterministic, but it depends on the load order rather than only on the documents, so results are
  reproducible for a given index and not for a given set of documents.

- **`SearchOptions.ParseQuerySyntax`**, default `true`. When `true` the query is read as query syntax:
  `"a phrase"` requires positional adjacency, `term*` is a prefix, `term~` is fuzzy. When `false` the
  query is literal text and the parser is not consulted. This is not a preference — a double quote is
  ordinary text in prose, and a query language cannot tell a quotation mark from a phrase delimiter.
  Measured on BEIR ArguAna, where all 1,406 test queries are whole arguments: 138 contain a straight
  `"`, and each had the quoted span turned into a mandatory phrase, after which no document but the
  query's own could satisfy it. 91 returned nothing at all and 20 returned a single result, at any
  retrieval depth. Set it to `false` for a corpus of natural-language queries.

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
  than as assertions. Re-recording is by hand: run `--verify-reference` to see what moved, read why,
  then edit `expectedNdcg` in the pinned file as a reviewed change. `--write` is accepted and
  deliberately writes nothing.
- **A CI workflow that replays the pins.** It exists because three documents promised this net and
  nothing ran it. It runs on every push that can move a score, and weekly: the repository pins no
  SDK, so the one drift a push cannot see is the runner's own, and until this it was not replayed
  again until some later commit happened to touch the library.
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
  reported **0.4061** on ArguAna — above the published figure — which the deduplicating engine
  could not have returned, and attributed the gap to a scoring rule that measurement then priced
  differently (+0.0705 claimed, +0.052 measured at matched parameters). The figure is named here
  so a reader carrying it can match it to this entry. The claim and the setting it rested on both
  landed on `main` **after** the `v0.6.0` tag — `QueryTermWeighting` is absent from it, and
  `git tag --contains` returns no tag for the commit that added it — so no published release ever
  asserted either. The measurement, the two conventions that were checked and ruled out, and the
  structural reason are in
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

- **`SearchOptions.Costs` — an opt-in per-query cost sheet, default `null`.** A `SearchCosts`
  instance records what one request cost: the tokens its query was parsed into, and per stage what
  that stage processed and how long it took. `RankedTextSearchEngine` records a `score` row;
  `RerankedTextSearchEngine` records `retrieve` and `rerank:<name>` on top of its inner engine's
  `score`, so a two-stage search reports three rows rather than two.
  It is the third half of a pair the library already had: a trace explains one search per document,
  a telemetry aggregates every search, and neither can say how two configurations compare on the
  same queries one query at a time. A sheet is per request and comes back with the results, so that
  comparison is two columns.
  A scoring stage's item count is **work rather than hits** — every document whose score was
  computed, including the ones that came back zero — which is deliberately not the `candidateCount`
  a telemetry reports. `Windows` is the column no library code fills, and says so in its own
  remarks: a scorer is a pure function of (document, query, index) and is handed no request, so
  window counts come from an engine, a decorator or a caller-side stage that has them in hand. The
  rows are bounded (`Capacity`, `Dropped`, `IsTruncated`) and the sheet is not thread-safe, like
  `SearchTrace`. With no sheet attached nothing is timestamped, built or allocated.
- **`WeightedCompositeScorer` — a scorer built out of scorers, `Σ wᵢ · componentᵢ`, in one pass
  over one candidate set.** For blending scores *inside* one scoring pass, where the weights are the
  only knob and every part is in one scale. That is a different operation from
  `WeightedScoreResultMerger`, which blends scores *across* finished rankings after normalizing
  each list by its own maximum — the right tool for sources whose scores are not commensurable, the
  wrong one for composing one score out of parts of itself, since a per-list maximum makes the blend
  depend on how many documents each list happened to return.
  Weights **may be negative**: that is how a part becomes a penalty, and `WeightedScoreResultMerger`
  refuses them because it blends non-negative normalized similarities. Only non-finite weights are
  rejected. A **zero weight removes** the part and the part is not called at all — because
  `0 × NaN` is `NaN`, so a part answering with a non-finite score must not be able to poison a weight
  saying it does not participate. A non-finite answer from a participating part propagates, so the
  engine rejects the document rather than reporting a score computed from a subset of the parts.
- **`DocumentLengthRatioScorer` — `|D| / avgdl`, the index's own document length over its own
  average.** One for an average document, above one for a long one. At a negative weight inside a
  composite it is a length prior, and it differs from BM25's `b` in kind: `b` damps a query term in
  a document that matched, in proportion to how much that term occurred, whereas this term is a
  property of the document alone — added to every document's score, matching or not, and scaling
  with no term. Two documents that matched identically can therefore be ranked differently by it.
  Used alone it ranks documents by length and nothing else, which is why it is a component and why
  it is not an `ITermOverlapScorer`; for the same reason a composite containing it cannot be one
  either, so a document sharing no query term receives a non-zero total and **can reach the page** —
  keep a matching part's weight above the priors', or compose inside a cascade stage so the
  candidates arrive from a retrieval stage that already decided what matched.
- **`WindowBm25Scorer` — score a document by its best window rather than by its whole text.** For a
  window `w` of width `s`, `Σ_t idf(t) · tf_w(t) · (k1 + 1) / (tf_w(t) + k1)`, and the document's
  score is the best over every window of every requested width. A window is a way of counting term
  frequencies, not a separate indexed unit: the document is returned whole and ranked against other
  documents, and nothing here chunks anything.
  The formula carries **no length normalization**, deliberately. A window's length is its own width
  by construction, so a term's damping inside it is decided by how many times the term occurs in
  those `s` positions and by nothing else — scoring by the best passage makes length stop being what
  ranks the document, and a caller who wants length to count puts it back as a term of its own with a
  weight they chose.
  `includeWholeDocument: true` scores the document as one window, which is BM25 with `b = 0` —
  **exactly**, asserted to the bit over a hand-built corpus, over 500 generated ones and over a
  measured corpus, so a windowed-versus-whole comparison made through this type compares one scorer
  against itself. **Combined with widths it changes nothing at all**, and that is the formula rather
  than a fault: with no length term a term's contribution rises with its frequency, the whole document
  dominates every window term by term, and a maximum over a set containing it can only be itself.
  Measured over 78,800 (query, document) pairs at `k1` = 0, 0.4, 1.5, 2.4 and 13.0 the gap is zero,
  and `WindowBm25ScorerTests` asserts it. **There is no multi-scale sweep to be had from this type**,
  and a fusion whose span set contains the whole document is a global scorer with extra steps.
  It stays an `ITermOverlapScorer`: a document sharing no query term has no position inside any
  window, so its score is `0` and the engine may skip it. It never takes the term-at-a-time
  accumulation pass, which scores by ordinal from a per-term weight and carries no positions — a
  price rather than a defect, and the reason the topology it belongs to is a cascade.
  **The width is the caller's decision, and the corpus's answer is not the same twice.** Measured on
  this type against its own whole-document counterpart — which `includeWholeDocument` makes BM25 at
  `b = 0` — the width chosen on a development split gave **+0.141 nDCG@10, interval [+0.092, +0.189]**
  on a corpus of meeting transcripts, and **+0.003, interval [−0.019, +0.026]** on one of book-length
  narrative, with no width excluding zero. The `b = 0` counterpart is the honest within-family
  comparison and not a strong scorer: against a whole-document BM25 tuned on the same development
  split, the same formula gains +0.035 on the first corpus and loses 0.036 on the second. Tuning
  **both** arms independently — the comparison that actually decides adoption — gives **+0.041,
  interval [+0.006, +0.079]** on the first and **−0.020, interval [−0.041, −0.001]** on the second,
  with both baselines at an interior optimum. Over the 180 windowed configurations swept on the first,
  the median against the tuned baseline is −0.003 and 48 % are positive; on the second, none of the
  180 is positive. So a caller who has already tuned `Bm25Scorer` should expect much less than +0.141,
  and should read one configuration's result as one corpus's result rather than as a property of
  windowing. The harness is outside this
  repository, so none of those figures is reproducible from it —
  [docs/ranking.md](docs/ranking.md#scoring-a-document-by-its-best-window-windowbm25scorer) carries
  them with the protocol, the corpora and the second baseline; `WindowBm25Scorer`'s remarks carry the
  rule and the pointer. Measure the sweep on the corpus you ship, choose the width on a split you then
  do not read, and read it per length band rather than as one average. No latency figure is quoted for
  this type, because none has been measured here.

- **A filtered query now uses the term-at-a-time pass by default.**
  `SearchOptions.AccumulateFilteredQueries` decides, and it now defaults to `true`; set it `false`
  for the previous behaviour. A query carrying `Filters` used to be declined by that pass and
  served by the per-document loop, which scores **every surviving candidate by document id** — one
  id-keyed length lookup plus one per query term. The pass scores by ordinal, as one walk of the
  posting entries that exist, and applies the filter to the ordinals it recorded.


[unreleased]: https://github.com/manuc66/LexiSharp/compare/v0.8.0...HEAD
[0.8.0]: https://github.com/manuc66/LexiSharp/compare/v0.7.0...v0.8.0
[0.7.0]: https://github.com/manuc66/LexiSharp/compare/v0.6.0...v0.7.0
[0.6.0]: https://github.com/manuc66/LexiSharp/releases/tag/v0.6.0
