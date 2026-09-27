---
title: Observability
nav_order: 10
---

# Observability

Three questions, three tools. *Why did this rank here?* — for one search, per document:
`SearchTrace` below, and `Explain` in
[Ranking](ranking.md#explainable-scoring-and-bm25-tuning). *How long, how many candidates,
which stage is slow?* — across all searches in production:
[`RetrievalTelemetry`](#observability-retrievaltelemetry). *Does the whole fleet agree, or
is one retriever insisting?* — for a fused page:
[`RetrievalAgreementAnalyzer`](#source-agreement-retrievalagreementanalyzer). Plus a
score-to-confidence mapping for gating a result set you are not sure about.

All of it is opt-in and dependency-free. A null telemetry, or a search without a trace,
allocates and records nothing.

## Ranking trace (`SearchTrace`)

`Explain` breaks down one scorer's arithmetic. `SearchTrace` goes further: it records **every stage
a document passed through**, so a final rank can be walked back stage by stage — which engine
scored it, what each merger contributed, what the reranker changed, what the boost did, and which
route ran.

```csharp
using LexiSharp.Core;

var trace = new SearchTrace();
var hits = engine.Search("refresh token", new SearchOptions(Limit: 5, Trace: trace));

foreach (var step in trace.Steps)
    Console.WriteLine($"{step.Stage,-6} {step.DocumentId,-8} {step.Before,8:0.000} -> {step.After,8:0.000}  {step.Detail}");

// route   d1              0.800 ->            dense (fallback: below threshold or no opinion)
// score   d1      4.819997 ->      4.819997  BM25
// merge   d1      0.016393 ->      0.016393  lexical=4.819997 dense=0.812003
// rerank  d1      4.819997 ->      0.912000
// boost   d1      0.912000 ->      1.824000  x2 +0
```

`SearchOptions.Trace` defaults to `null`, and a null trace is inert: no engine records, allocates
or formats anything. Pass one and each stage appends to it as it runs, so a decorated pipeline
accumulates its whole chain.

**Cost — what is measured and what is not.** Measured with BenchmarkDotNet's `MemoryDiagnoser` and
stable across repeated runs: a saturated trace allocates **0 additional bytes** per search
(1.34 KB either way), and a trace created per search costs **~0.47 KB** (the object plus its
backing array).

A **time** figure is deliberately **not** given, and the reason is itself a measurement. On the
machine used for the run the *baseline* benchmark varied between 1.52 ms and 3.57 ms (2.3×) across
identical runs, BenchmarkDotNet reported bimodal distributions on the pre-existing search benchmarks
too, and the traced variant came out faster than the baseline in one run — which is impossible. A
delta under that spread is not measurable there, so quoting one would be fiction.

Full details, machine configuration and the reproduce command: [Benchmarks](benchmarks.md#ranking-trace-searchtrace--allocation-only-no-timing).

**Bounds.** A trace stops recording past `Capacity` (default 256) and counts the overflow in
`Dropped`; check `IsTruncated` rather than assuming `Steps` is the whole story. Stage recording is
bounded by the page or the candidate shortlist, never by the corpus, so a trace cannot grow with
the index — this is what the `ScoreStageIsBoundedByThePageNotTheCorpus` test pins on a 20 000-doc
index.

**Not thread-safe.** A trace is a mutable collector: give each concurrent search its own, the same
way each gets its own `SearchOptions`. The telemetry below is the opposite case — immutable and
stateless per query, so one instance is shared.

## Observability (`RetrievalTelemetry`)

`SearchTrace` explains **one search**. `RetrievalTelemetry` answers the operational question —
over time, in production, across all searches: how long they take, how many candidates they touch,
how big the index is, and which stage is responsible when a query is slow.

It is opt-in and dependency-free. Attach a telemetry to an engine and it reports to a
`RetrievalLog` sink, an `IRetrievalMetrics` collector, or both:

```csharp
using LexiSharp.Core;

var metrics = new InMemoryRetrievalMetrics();

var engine = new RankedTextSearchEngine(
    index, new Bm25Scorer(),
    telemetry: new RetrievalTelemetry(metrics: metrics));

engine.Search("refresh token");

foreach (var e in metrics.Snapshot().Engines)
    Console.WriteLine($"{e.Engine}: {e.SearchCount} searches, {e.MinElapsedMs:0.###}–{e.MaxElapsedMs:0.###} ms");
```

With `LexiSharp.AspNetCore`, an `ILogger` sink is one call away:

```csharp
using LexiSharp.AspNetCore;

var telemetry = RetrievalLogger.Telemetry(
    loggerFactory.CreateLogger("search"), RetrievalLogLevel.Information);
```

Events carry the engine name, a stable event id (`search` / `stage` / `index`), a
self-contained message and structured `ElapsedMs` / `Count` fields, so a log aggregator can build
dashboards without parsing prose. Per-stage events are `Debug`, so the default `Information`
threshold keeps one line per query and drops the per-stage detail.

**Instruments by pipeline stage.** `HybridTextSearchEngine` reports one `source` stage per delegate
(labelled by your `sourceNames`, so a slow lane is named the way your application named it) and one
`merge` stage. `RerankedTextSearchEngine` reports `retrieve` and `rerank:<name>` — the cross-encoder
call is usually where the latency lives, and it warns above 100 ms. `BoostedTextSearchEngine`
reports `retrieve` and `boost`. A query against an empty index, or one whose terms match nothing in
the vocabulary, is a `Warning` rather than a silent zero-result page.

**Cost when unused.** `RetrievalTelemetry.None` reports `IsEnabled == false`, and every instrumented
engine reads that once per search before touching a timer, so an un-instrumented engine takes no
timestamp, allocates nothing and calls nothing. A `RetrievalTelemetry` is immutable and holds no
per-query state, so concurrent searches share one safely as long as the sinks are. Note this is a
statement about the code, not a measurement: no timing figure is quoted here for the same reason
the trace section above quotes none.

**What is *not* covered.** `IRetrievalMetrics` is the only number surface: there is no built-in
OpenTelemetry or Prometheus exporter, and no percentile histogram — `InMemoryRetrievalMetrics` keeps
a count, a total, a min and a max per engine and stage, which is enough to assert on in tests and to
back a diagnostics endpoint, and is deliberately not a substitute for a real metrics pipeline.
Implement `IRetrievalMetrics` over your own backend for that. `SearchTrace.StageCounts()` reports how
many steps each stage recorded; per-stage *timings* come from the metrics collector, not the trace.

A `LexiSharpIndexHealthCheck` reports readiness for the same reason: an index holding nothing
answers every query with zero results, which is a silent failure rather than an obvious one.

```csharp
services.AddLexiSharpRetrievalMetrics();
services.AddLexiSharpSearchHealthCheck(
    _ => index,
    o => o.Tags = ["ready"],
           o.Probe = ct => connection.OpenAsync(ct));  // optional: backing-store reachability
```

## Source agreement (`RetrievalAgreementAnalyzer`)

A fused ranking hides *why* a document is on the page. `RetrievalAgreementAnalyzer` reads the
per-source scores in `DetailedSearchResult.Contributions` and classifies each document by how much
its sources agree:

```csharp
using LexiSharp.Core;

var page = hybrid.SearchWithDetails("refresh token", new SearchOptions(Limit: 20));
var reports = RetrievalAgreementAnalyzer.Analyze(page);

foreach (var report in reports)
    Console.WriteLine($"{report.DocumentId}  {report.Agreement}  strong: {string.Join(",", report.StrongSources)}");

var counts = RetrievalAgreementAnalyzer.Summarize(reports);
```

| Category | What it means |
|----------|---------------|
| `Unanimous` | several sources returned it, all found it convincing |
| `Disputed` | several returned it, they disagree — some convinced, some not |
| `Lukewarm` | several returned it, **none** convinced: it is on the page through a merger, not anyone's conviction |
| `SingleSource` | exactly one source returned it — the case worth surfacing, since a reader of the final score cannot tell |
| `None` | no source returned it; should not occur on a result page |

The categories are structural, not named, because source labels are yours: a setup calling them
`lexical`/`dense` and one calling them `bm25`/`cosine` describe the same shape, so a hard-coded
`LexicalOnly` would be wrong for half of them. `StrongSources` gives the names back.

**Normalization is not optional.** Raw contributions are not comparable across sources — LexiSharp's
dense lane returns a cosine in [0, 1] while its BM25 lane returns values around 1 to 10. Dividing
each contribution by the best score that source gave on the same page makes the values scale-free
without assuming anything about the scoring function. A source that returned nothing is reported as
`AbsentSources` rather than weak, because being outside a source's depth is a different fact from
being ranked low by it.

**The threshold is a heuristic.** `strongThreshold` (default `0.5`) is the share of a source's best
score at which a document counts as strongly supported. It is a round number chosen for
readability, **not a value validated against relevance data** — tune it against your own corpus, and
pass `sourceNames` so a source that silently contributed nothing still shows up as absent.

The demo is the quickest way to see the distinction matter. Running its six sample queries over its
26-document corpus, the fused page holds 9 `unanimous`, 8 `disputed`, 7 `singleSource` and
2 `lukewarm` — the obvious matches are unanimous, the arguable ones disputed, the lukewarm ones are
on the page through the merger with nobody enthusiastic, and some ride on a single lane. `None`
does not appear, and cannot: it classifies a document no source returned, so by definition it never
shows up on a result page.

## Calibrated confidence (`ScoreConfidence`)

BM25 output is not a probability: a score of 4.8 means nothing on its own, and the scale
differs per scorer. `ScoreConfidence` maps a result set's raw scores to one confidence in
`[0,1]` per result, so a result set can be gated by an application-level
`minConfidence` threshold that raw lexical scores cannot express:

```csharp
using LexiSharp.Ranking;

// Per result, in ranking order. WinnerMargin is the default.
IReadOnlyList<double> confidences = ScoreConfidence.Compute(results);

// The one to compare against a gate. 0 for an empty result set.
double top = ScoreConfidence.TopConfidence(results, ScoreConfidenceMethod.ZScore);
```

| Method | What it measures | Blind to |
|---|---|---|
| `WinnerMargin` (default) | the gap to the next result, `1 - score_next / score_current`; the last result is `0` | the absolute quality of the match — a flat ranking of high scores still looks unsure |
| `ZScore` | each score's position in the set's own distribution, squashed through the logistic function; an all-tied set collapses to a neutral `0.5` whatever its scale | the scale itself, so two corpora are not comparable |

A score of exactly `0` means *not a match* by engine convention, so such results — and any
non-finite score — are never confident. Read the name carefully: this is a **relative
confidence in a result set, not a calibrated probability of relevance**. The threshold is
yours to choose, and nothing here was fitted against relevance judgements.
