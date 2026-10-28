---
title: Allocation and latency benchmarks
nav_order: 12
description: >-
  Allocation and latency measurements from BenchmarkDotNet, with the machine they
  were taken on and the command to reproduce them.
---

# Allocation and latency benchmarks

Numbers on this page come from BenchmarkDotNet runs on a single, aging developer machine —
they are **indicative only**, not a performance claim. Run the suite yourself before drawing
any conclusion about your own corpus and hardware.

## Machine & configuration

```
BenchmarkDotNet v0.14.0, Manjaro Linux
Intel Core i7-4790 CPU 3.60GHz (Haswell), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.111
Runtime: .NET 10.0.11 (X64 RyuJIT AVX2)
Job: default (not ShortRun) — measurements below were collected in a single suite run.
```

## Search (`SearchBenchmarks`)

Fast, non-noisy representative of the search hot path: a 10,000-document, 50-word corpus
(index built once before the loop; `Allocated` is per-query, not the corpus itself):

Host: BenchmarkDotNet v0.14.0, Manjaro Linux, Intel Core i7-8850H CPU 2.60GHz (Coffee Lake),
12 logical cores, .NET SDK 10.0.111, Runtime .NET 10.0.11, DefaultJob — the host named in
[Term-at-a-time scoring](#term-at-a-time-scoring), not the one at the top of this page.

| Method                          | Mean      | Error     | StdDev    | Allocated |
|-------------------------------- |----------:|----------:|----------:|----------:|
| Bm25Search                      |  121.6 µs |  2.38 µs |  2.23 µs |     672 B |
| TfIdfSearch                     |  104.8 µs |  1.58 µs |  1.47 µs |     944 B |
| QueryLikelihoodSearch           | 2,012.7 µs | 31.53 µs | 44.20 µs |   1,127 B |
| BooleanSearch                   | 1,099.6 µs | 21.90 µs | 57.30 µs |     955 B |
| Bm25SearchRunAllQueries         |   861.6 µs |  9.83 µs |  8.72 µs |   3,402 B |

Ranking output of every configuration is verified byte-for-byte against the pre-optimization
engine (`QueryPlanParityTests`, full suite green), so the speedups below come with no quality
trade-off. On the nfcorpus evaluation, all nDCG@10 scores are identical to the pre-optimization
baseline (BM25 0.308, TF-IDF 0.248, Query-Likelihood 0.288, Dense 0.304, hybrids 0.323/0.333).

### Ranking trace (`SearchTrace`) — allocation only, no timing

`SearchTrace` is opt-in through `SearchOptions.Trace` and defaults to `null`. The rows below exist
to price what that null costs, and they were **not** measured on the machine described above — this
run was made on a different host, in a sandbox that hid all but one core and forced
BenchmarkDotNet into `--inProcess`:

```
Intel Core i7-8850H CPU 2.60GHz (Coffee Lake), 1 CPU visible to BDN
Job: ShortRun and MediumRun, --inProcess (a separate process could not be launched)
```

| Variant                           | Allocated per search |
|-----------------------------------|---------------------:|
| `Trace: null` (default)           |             1.34 KB  |
| `TraceSaturated` (at capacity)    |             1.34 KB  |
| `TracePerSearch` (fresh per call) |             1.81 KB  |

**Allocation is the trustworthy column.** It came out identical in every run and at every job: a
saturated trace adds **0 bytes**, and a trace created per search costs **~0.47 KB** — the
`SearchTrace` object plus the backing array for its ten `TraceStep`s. Recording happens on the cut
page rather than per candidate, which is why the cost follows the page size and not the corpus.

**No timing is quoted, and that is a finding rather than an omission.** On this host the *baseline*
`Bm25Search` itself measured anywhere from 1.52 ms to 3.57 ms across identical runs, a 2.3× spread.
BenchmarkDotNet additionally reported bimodal and multimodal distributions on the pre-existing
search benchmarks, not only on the trace ones, and `TracePerSearch` came out *faster* than the
baseline in one run, which is physically impossible. A delta below that spread is not resolvable
here, so quoting one would be invention. Both benchmarks are in `SearchBenchmarks`; running them on
a quiet multi-core machine is what would produce a publishable timing.

The `Search allocation` workflow (`search-allocation.yml`, `workflow_dispatch`) re-measures this on
a second host when you dispatch it, and uploads the raw report with the machine configuration next to
it. **Read only its `Allocated` column.** Allocation is counted in bytes and does not depend on how
fast the host is, so that number travels; the `Mean` column does not. These are Azure VMs shared
between tenants, where host-level contention is a documented source of variance, and the effect
being measured here is a few percent — a shared runner cannot resolve it, and a `Mean` copied from
that artifact into this file would not be a measurement. The workflow gates nothing, deliberately, and
runs on no schedule: it has no committed baseline to compare against and no threshold to trip, so its
reader is a person opening a run.

What *does* gate, deterministically, is `lexisharp verify` in the `core` CI job: it replays this
corpus against the committed baseline and fails the build on any drift in ranking or metrics. No
clock is involved, so there is nothing to be noisy — which is the difference between a benchmark
and a regression test, and the reason behaviour is protected that way rather than by a threshold.

### Merge explanation on a traced hybrid search — allocation, before/after

`HybridTraceBenchmarks` prices the query-level merge step on a two-engine hybrid (2 000 synthetic
documents, `Limit: 10`, lexical + dense, `WeightedScoreResultMerger`). Measured on the host above
with the parent commit compiled in a separate worktree and the two builds **alternated in one
session**:

| Build | `Trace: null` | Traced |
|---|---:|---:|
| parent commit (no explanation) | 60.1 KB | 71.0 KB |
| with the explanation | 60.1 KB | 72.4 KB |

**The untraced path allocates the same bytes, to the byte.** That is the column that matters, and
it is not a timing claim: the explanation is written below the `trace is null` guard in
`RecordMergeStage`, so no line of it runs when nobody asks. The traced path pays **1.4 KB per
search** — one step and one formatted string, against the ten steps recording already writes per
document.

The timing rows are deliberately not reproduced here. On this host two runs of the *same* build for
the same untraced case read 318.8 µs and 325.7 µs (2 %), while the parent build read 314.8 µs —
run-to-run drift larger than the effect, and the machine was shared with another session. The
allocation counter does not have that problem.

```bash
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- --filter "*HybridTraceBenchmarks*"
```

### Before / after the search optimization pass (the 'after' build is commit 639880e, same env)

| Method                          | Before          | After          | Mean Δ   | Alloc Δ   |
|-------------------------------- |----------------:|---------------:|---------:|----------:|
| Bm25Search                      |  6.923 ms / 4.12 MB |  2.336 ms / 1.33 KB | −66 % | −99.97 % |
| TfIdfSearch                     |  6.239 ms / 4.12 MB |  2.197 ms / 1.3 KB  | −65 % | −99.97 % |
| QueryLikelihoodSearch           |  7.062 ms / 4.12 MB |  3.009 ms / 1.35 KB | −57 % | −99.97 % |
| BooleanSearch                   |  1.922 ms / 1.07 MB |  1.077 ms / 1.19 KB | −44 % | −99.89 % |
| Bm25SearchRunAllQueries         | 47.421 ms / 20.74 MB | 24.283 ms / 6.87 KB | −49 % | −99.97 % |

The three big wins in the pass: a candidate-restricted scoring path for range scorers, a
bounded top-L insertion instead of a full sort, and a per-query plan that hoists corpus
statistics (idf, collection probabilities, average length) out of the per-document loop.

## Where a query's bytes go (`QueryAllocationBreakdownBenchmarks`)

`SearchBenchmarks` reports one figure per scorer. This class takes that figure apart: each row
isolates one component of a search, so a reduction attempt knows which component to aim at.

Host: BenchmarkDotNet v0.14.0, Manjaro Linux, Intel Core i7-8850H CPU 2.60GHz (Coffee Lake),
12 logical cores, .NET SDK 10.0.111, Runtime .NET 10.0.11, DefaultJob, `MemoryDiagnoser`. The
corpus is the same 10,000 × 50-word one as `SearchBenchmarks` (38-word vocabulary), the query is
"search engine" (two terms), the scorer is `Bm25Scorer`. Read the `Allocated` column only — the
same instruction the rest of this page carries.

| Method                | Allocated |
|-----------------------|----------:|
| `Tokenize`            |    264 B  |
| `TokenizeOneTerm`     |    224 B  |
| `Parse`               |    312 B  |
| `SearchHitLimit1`     |    312 B  |
| `SearchHitLimit10`    |    672 B  |
| `SearchHitLimit100`   |  4,273 B  |
| `SearchMissLimit10`   |    840 B  |
| `SearchPhraseLimit10` |  1,425 B  |
| `SearchLiteralLimit10`|    672 B  |

**Two rows do not reproduce under this diagnoser, and that is a property of the rows rather than
a mystery.** `Tokenize` has read 264 B, 2,308 B and 7,078 B, and `Parse` has read 312 B and 0 B,
for identical code across runs of this class; in the runs where `Allocated` reads 0 the Gen
columns still show allocation happening. Five of the six search rows reproduce byte for byte; the
phrase row has read 1,304 / 1,307 B across runs — read it as ≈1.31 KB.
The values shown are the ones that reproduce, and the tokenizer's split is confirmed by
`TokenizeOneTerm`: 224 B for one term against 264 B for two is 184 B of list plus 40 B a token.

**What the rows say.**

- **The tokenizer and the parser: 312 B through `Parse`.** `TokenizeOneTerm` at 224 B against
  `Tokenize` at 264 B separates the tokenizer's fixed cost from its per-token cost: 184 B for the
  list it builds into and 40 B a token. `Parse` adds the `ParsedQuery` record — 48 B — to that. A
  plain query is scored without either: `ISpanTokenizer.TokenizeNormalized` fills a buffer of
  `NormalizedTerm`s — a slice of the query for a term the query already holds, a materialized string
  for one that folding, stemming or trimming replaced — and the index resolves each slice by span.
  The `Tokenize` and `Parse` rows above describe the string pipeline a caller of those methods
  still pays for; a search pays none of it.
- **The fixed cost: 272 B.** Three page sizes on one query fall on one line — 312 / 672 /
  4,273 B — so everything a query costs before it returns anything is 272 B: the tokenization into
  slices, the query plan, the top window and the per-query markers, not split further here. The
  per-candidate work allocates nothing: the score arrays and the slice buffer are rented and
  returned, so a steady stream of searches allocates nothing after the first few.
- **A returned row: 40 B.** The slope of that line — a window entry of 16 B and a `SearchResult` of
  24, which is a value type and so sits in the page's array rather than behind a reference to an
  object of its own. Of the 656 B a two-term search returns, 400 B is the page of ten: **61 % of the
  figure is the shape of the answer**, not the engine's work. A caller that can take its page into a
  buffer of its own has more left to gain here than any change to the scoring path.
- **The paths price differently.** A query matching nothing takes the candidate path with the string
  tokenizer — it is too rare to amortize the accumulation buffer — and costs 840 B, *more* than the
  672 B of a query that returns a page: the page became the cheap part, and the string path did not.
  A quoted phrase is not a plain query, so it keeps the string path and gates every candidate
  positionally: 1,425 B. `ParseQuerySyntax: false` is plain by construction and costs exactly what
  the parsed one does.

**What is pinned.** `QueryAllocationBreakdownTests` holds the budgets a regression would break: the
tokenizer under 512 B, a two-term search under 2 KB, a returned row under 128 B, a query matching
nothing under 2 KB. A benchmark reports the number; the test refuses to let it come back.

Reproduce:

```bash
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- --filter '*QueryAllocationBreakdown*'
```

## Index building (`IndexBenchmarks`)

Building an `InMemoryTextIndex` from scratch, 50 words per document. Gen0/Gen1/Gen2 columns
are GC collection counts per 1,000 operations (BenchmarkDotNet convention):

| Method                            | DocumentCount | WordsPerDocument | Mean      | Error    | StdDev   | Gen0      | Gen1      | Gen2     | Allocated |
|---------------------------------- |-------------- |----------------- |----------:|---------:|---------:|----------:|----------:|---------:|----------:|
| BuildInvertedIndex                | 1000          | 50               |  10.30 ms | 0.200 ms | 0.280 ms |  265.6250 |  171.8750 |        - |   6.63 MB |
| BuildIndexWithStopWordsAndStemmer | 1000          | 50               |  10.89 ms | 0.217 ms | 0.468 ms |  265.6250 |  171.8750 |        - |   6.67 MB |
| BuildInvertedIndex                | 10000         | 50               | 167.31 ms | 3.331 ms | 4.882 ms | 1500.0000 | 1250.0000 | 500.0000 |  64.94 MB |
| BuildIndexWithStopWordsAndStemmer | 10000         | 50               | 164.42 ms | 3.224 ms | 5.207 ms | 1750.0000 | 1500.0000 | 750.0000 |  65.34 MB |

Read at this scale (roughly): a 10k-document, 50-word corpus indexes in ~0.16 s and allocates
~65 MB in the process. Nothing is claimed here about larger corpora, search latency under
load, or memory behavior beyond a single run.

## Second pass: SIMD, shared term instances, bounded windows

Same machine, same corpus as above. **Method matters more than usual here.** The host drifts by
±10–16 % between process launches, so "7 rounds of A, then 7 rounds of B" produced a
confident-looking +10 % regression on BM25 that a three-way interleaved run in a single session
showed was noise. Every timing row below comes from binaries alternated **within** one loop, and
the byte counts are the load-bearing evidence, because they do not depend on the clock.

| What                             | Baseline   | After     | Delta      |
|----------------------------------|-----------:|----------:|-----------:|
| Dense search, 10k docs × 384-dim |  5,728 µs  |  1,257 µs | **−78 %** |
| Dense search allocation          | 424,568 B  |   3,008 B | **−99.3 %** |
| `InMemoryTextIndex` retained     |  58.91 MB  |  40.88 MB | **−30.6 %** |
| Index build, retained            |  52.08 MB  |  35.85 MB | **−31.2 %** |
| Index build, allocated           |  82.58 MB  |  86.93 MB | +5.3 %     |
| Index build, time                |   ~160 ms  |   ~159 ms | no change  |
| BM25 search allocation           |   1,360 B  |   1,280 B | −5.9 %     |
| BM25 search time                 |  ~2.9 ms   |  ~2.9 ms  | no change  |
| Naive Bayes `Predict`            |   9.5 µs   |   7.2 µs  | −24 %      |
| Naive Bayes `Predict` allocation |   2,385 B  |   2,505 B | +5.0 %     |

**Dense search, 4.5×.** `VectorSimilarity` runs on `Vector<float>` (AVX2/AVX-512 where the host
has it) with two independent accumulator chains, and the engine computes each document's squared
norm once at insert time, so a scan costs a dot product per document instead of a dot product
plus two norm accumulations. Vectors live in one contiguous dimension-strided block, so a scan
walks memory linearly rather than a dictionary entry per document. The page is cut by a bounded
worst-first window rather than materializing and sorting every document that scored above zero,
which is where the 99.3 % allocation drop comes from: the old path built a `SearchResult` for all
10,000 documents and a LINQ sort chain on top, to return 10 rows. The worst "after" sample
(2,206 µs) still beats the best "baseline" sample (4,900 µs), so the distributions do not overlap.

**Index size, −31 %.** The tokenizer allocates a fresh string per token occurrence, so an index
built straight from it holds one string object per *token* — 500,000 objects to represent 38
distinct words on this corpus. `InMemoryTextIndex` keeps one instance per term in its posting list
and rewrites each occurrence to it, so the corpus holds a vocabulary. It costs no extra hashing,
because resolving a term to its posting list is a required lookup anyway; the shared instance rides
along. Build allocation rises 5.3 % (a second, exactly-sized token list per document) and build
time is unchanged within this host's resolution.

**Lexical search: unchanged, and that is a measurement rather than an absence of one.** The pass
touched the scoring path only through the shared top-L window. It allocates 5.9 % less per search
and, timed, is indistinguishable from the baseline in a three-way interleaved run (minima
2,920 / 2,945 / 2,877 µs for baseline / with-window / with-window-reverted). The third build
exists to make that check falsifiable.

**Naive Bayes, −24 %.** The per-token idf weight and vocabulary flag were a dictionary lookup per
(class, token) pair, recomputed once per class inside the scoring loop. They are resolved once per
prediction into a single array, and the corpus count is materialized only when `Complement` is on,
so a default model pays for one array rather than five. Costs 5 % more allocation.

**The vocabulary membership lookup is unconditional.** Making it conditional on
`SkipOutOfVocabularyTokens` is wrong — `IdfWeight` reads the document-frequency table and needs the
answer whatever the options say, so an out-of-vocabulary query term would throw
`KeyNotFoundException` on any model with a non-default `IdfMode`. Membership is resolved
unconditionally and the flag array is only materialized when `SkipOutOfVocabularyTokens` is on. The
cost is one `TryGetValue` per query
token, and it is **not** measurable above noise on this host: 7 interleaved rounds of 40 000
`PredictBest` calls give 2.025 / 2.034 / 2.047 µs per call with the lookup against
1.911 / 2.046 / 2.015 µs without, i.e. overlapping. The −24 % stands; treat the per-token
lookup as free, because that is how it measured, not because it was argued to be.

Behaviour is unchanged: the full test suite passes and `lexisharp verify` reports 132 unchanged,
0 changed against the golden master. The `VectorSimilarity` kernels change the summation order,
which the existing property tests cover with their tolerances (1e-6 symmetric, 1e-3
scale-invariant).

### Candidate enumeration (`CandidateSearchBenchmarks`)

`SearchBenchmarks` cannot see this path. Its corpus is 38 words, so every term lands in ~74% of
documents, the summed document frequencies exceed half the corpus, and the engine's own cost
comparison concludes that scanning beats enumerating — so it scans. That makes those rows blind to
candidate generation, which is the path real queries take because real queries are made of rarer
words. `CandidateSearchBenchmarks` runs on a Zipf corpus (10,000 × 50 words, 30,000 terms, s = 1.07)
and queries the tail.

Before/after the unordered-candidate change, **min of 5 runs per side**, two binaries alternated
within one loop (`--job short --inProcess`), 12 logical cpus, load average 2.1–5.1 throughout:

| Method        | A: ordered | B: unordered | Ratio  | Allocated A / B |
|---------------|-----------:|-------------:|-------:|----------------:|
| `OneTerm`     |     385 ns |       395 ns |   1.0×  |    832 B / 832 B |
| `TwoTerms`    | 153,342 ns |       722 ns | **212×** |  1,161 B / 1,152 B |
| `ThreeTerms`  | 158,473 ns |       846 ns | **187×** |  1,217 B / 1,208 B |

The two distributions do not come close to touching: the worst A sample (168,218 ns) is 163× the
best B sample (1,027 ns).

`OneTerm` is the control, and it is the row that makes the other two trustworthy. With one query
term the engine takes *the same code path in both builds* — a single posting list already comes out
in corpus order, so there is no union and no ordering pass to skip — and the two sides differ by
10 ns, ~2.5%. That is the harness's resolution floor on this host, and it is the same order as the
`OneTerm` difference, so `OneTerm` is reported as unchanged rather than as a 2.5% regression.

**What the change is.** `ICandidateIndex.GetCandidateDocuments` documents that its result is in
corpus order, and honouring that costs a walk of the *entire* corpus after the union of the posting
lists — O(corpus) however few documents matched. A 3-term query whose candidate set is 2 documents
out of 10,000 still pays for 10,000 documents. The engine asks for the set through an internal
`IUnorderedCandidateIndex` capability, and the walk is gone. The public method keeps its documented
order, so this is the engine declining to pay for it, not the contract being narrowed.

This is safe because the ranking order is total and does not depend on candidate order:
`TopRankedWindow` orders by score descending with ties broken by ordinal document id — documented, and
checked by `CandidateEnumerationOrderTests`, which builds a corpus of 100
documents that all score *exactly* the same and runs the query twice, with the candidates handed
back ascending and then descending, asserting the two pages are identical — comparing the two runs
rather than asserting the expected ids, because a window that is order-dependent can still get the
right ids for one particular order (a « keep the last N offered » window returns the ten smallest
ids descending and the ten largest ascending, so a single-order assertion passes on half the broken
implementations). Three mutations were run against the suite and each is caught: removing the id
tie-break (4 tests fail), sending the engine back to the ordered path (1 fails), and dropping
documents from the unordered union (5 fail).

**This is a time win, not an allocation win** — the table's last column is essentially flat, because
the removed pass was enumerating an existing dictionary and allocated nothing. The `Allocated`
column being the load-bearing evidence elsewhere on this page does not apply here. Note also that
the *baseline* `SearchBenchmarks` numbers earlier on this page remain valid and unaffected: that
corpus never entered the candidate path.

### Term-at-a-time scoring

The 20× in the last subsection of this section — a 2-term query over the 38-word corpus — sits in a
regime real queries never enter, which is why the corpus here is a Zipf distribution rather than the
38-word one at the top of the page. The ratio itself is real.

**Host.** Different from the one named at the top of this page, and every number in this section
comes from it — nothing here is comparable with a table above:

```
BenchmarkDotNet v0.14.0, Manjaro Linux
Intel Core i7-8850H CPU @ 2.60GHz (Coffee Lake), 12 logical cores
.NET SDK 10.0.111 · Runtime: .NET 10.0.11 (X64 RyuJIT AVX2)
Job: default (ShortRun where stated)
```

Each before/after pair is **two BDN processes run back-to-back on this host**, which is what makes
the ratio meaningful: the two sides share a machine state, and neither absolute figure is
comparable with anything else on this page.

**What the change is.** Every range scorer was document-at-a-time: `Bm25QueryPlan.Score` asks
`index.TermFrequency(documentId, term)` for each (document, term) pair, and that is two
string-dictionary lookups — one to resolve the term's posting list, one to resolve the document
inside it. With `m` terms over `n` candidates that is `2·m·n` string hashes, and the term half of it
is the same lookup repeated for the same `m` values on every one of the `n` documents.

A scorer whose score decomposes into a per-term contribution depending on nothing but that
document's **term frequency and length** folds the inverted lists straight into an
ordinal-indexed score buffer: one walk of each term's postings, no id hashed anywhere. BM25,
BM25+, BM25L and TF-IDF qualify. BM25F (per-field geometry) and query likelihood (a `log P(t|d)`
term for documents that do *not* contain the query term) do not, and keep the per-document loop.

Three things make the two loops produce the *same doubles* rather than merely the same ranking —
which matters because `TopRankedWindow` breaks ties with `==` on the score, so a last-bit difference
reorders the page:

- Terms are folded in **query order**, one posting list at a time, so each document's contributions
  are summed in the order the per-document loop summed them. IEEE-754 addition is deterministic
  given an order, so the sums come out bit-identical.
- `QueryPlanParityTests` asserts that for 10 scorer configurations × 5 queries, and
  `Engine_ResultsAreIdenticalWhicheverScoringPathItTakes` asserts it again through the public
  surface, page for page, on a corpus deliberately full of exact ties.
- The accumulator's buffers come from `ArrayPool`, which is a **process-wide** pool, so the
  recorded-flags array is cleared on rent. Skipping that clear made the suite fail
  *intermittently* — different tests on different runs of the parallel suite, all passing in
  isolation. It is the kind of defect a benchmark would never surface, and
  `ScoreAccumulatorTests` pins it: remove the clear and that test fails, `Count` 0 against 512.

**Search, head terms** (`CandidateSearchBenchmarks`, Zipf 10,000 × 50 words, s = 1.07). This is
the representative table: a real term distribution, and the three head rows are the ones that
exercise the regime the 38-word corpus cannot reach, because there every term lands in ~74 % of
documents — analytically (37/38)^50 ≈ 0.264 — and the engine full-scans instead of enumerating. That
74 % is inherited from the existing benchmark comments, not re-measured here, and it is the number
the next table below is *not* about.

| Method           | Before      | After       | Speedup | Allocated before / after |
|------------------|------------:|------------:|--------:|-------------------------:|
| `OneTerm`        |    385.8 ns |    370.0 ns |   1.04× |     832 B / 848 B |
| `TwoTerms`       |    700.2 ns |    695.7 ns |   1.01× |   1,152 B / 1,152 B |
| `ThreeTerms`     |    813.2 ns |    783.7 ns |   1.04× |   1,208 B / 1,208 B |
| `HeadTerm`       |  919,532 ns |  121,631 ns |  **7.6×** |   1,217 B / 1,224 B |
| `TwoHeadTerms`   | 1,393,805 ns |  171,087 ns |  **8.1×** |   1,267 B / 1,272 B |
| `ThreeHeadTerms` | 2,949,926 ns |  190,469 ns | **15.5×** |   1,313 B / 1,320 B |

The three tail rows are at parity — slightly faster, in fact — and that is the point of them: they
are the queries the new loop is *not* meant to win, and it does not cost them anything measurable.
The `+16 B` on `OneTerm` is the only cost measured anywhere in this section and **its cause is not
identified**.

**What is verified about these figures, and what is not.** The query plan receives the raw term list
rather than the deduplicated one, so that a repeated query term reaches the scorer (see
`QueryTermWeighting`); every timing in the table above was taken before that change. The `Allocated`
column reproduces **byte for byte** on all six rows — 848 / 1,152 / 1,208 / 1,224 / 1,272 / 1,320 B —
which is what the reasoning predicts: `TermDeduplicator.Distinct` returns its input when it can prove
there is no duplicate, and none of these six queries repeats a term, so the plan receives the same
terms in the same order.

The **timings are not claimed to describe the current build.** A fresh run would not validate the
numbers above, it would replace them: the drift between two runs of the same code on this host is
larger than the effect a re-measurement is looking for, so validating a figure requires measuring
before and after in one session. What is claimed instead is narrower and
checked: the scored path is identical, not merely similar. Scoring the same eight queries against the
two builds — repetitions up to forty — gives **bit-identical** scores in round-trippable form, and
the golden master is unchanged at 132 rankings with no tie reordering.

**The speedup tracks the cost of the per-document loop, not the query's rarity as such.** Per unit
of work: the document-at-a-time loop costs ~96 ns per document it scores (one `DocumentLength`
hash plus one `TermFrequency` per query term, each a string hash), and the term-at-a-time pass
costs ~13 ns per posting entry. That ~7× slope is the mechanism; the ratio reaches 10-15× at the
head because the term-at-a-time path's fixed cost — clearing a corpus-sized buffer — has stopped
mattering by then, and is *below* 1× at df = 1 for the same reason. So a query that takes 200 ns does
not gain, and a query that takes 1.6 ms does, which is a different and more useful statement than
« it helps if your traffic has frequent terms ».

**Search, the degenerate corpus** (`SearchBenchmarks`, 38 words, so every row walks 10,000
documents). Quoted second and read as the upper bound it is: a corpus where *every* term is a head
term is not a distribution any real query log resembles.

| Method                    | Before      | After       | Speedup |
|---------------------------|------------:|------------:|--------:|
| `Bm25Search`              |  1,629.1 µs |    152.4 µs | **10.7×** |
| `TfIdfSearch`             |  1,265.4 µs |    135.1 µs |  **9.4×** |
| `QueryLikelihoodSearch`   |  2,139.2 µs |  1,848.5 µs |   1.16× |
| `BooleanSearch`           |    864.8 µs |    862.0 µs |   1.00× |
| `Bm25SearchRunAllQueries` | 14,656.8 µs |  1,022.3 µs | **14.3×** |
| `TraceSaturated`          |  1,504.1 µs |    151.1 µs |  **9.9×** |
| `TracePerSearch`          |  1,417.0 µs |    161.5 µs |  **8.8×** |

The two control rows are `QueryLikelihoodSearch` and `BooleanSearch`: neither scorer has a
separable per-term contribution, so neither takes the new loop, and **neither is claimed as a
result**. `BooleanSearch` is at parity within this host's run-to-run noise — three runs each side
gave 1,023 / 1,010 / 960 µs after and 1,042 / 1,044 / 1,349 µs before, so the distributions
overlap almost entirely. `QueryLikelihoodSearch`'s 1.16× is **most likely noise too**: no mechanism
for a real gain on that path has been identified, and the index changes make its per-document work
marginally *heavier*, not lighter (one extra array read for `DocumentLength`). It was **not** at
parity on the first attempt of the change, when the posting lists were keyed on a document object
and cost that path a third dictionary probe: measured 1,714 µs against a 1,073 µs baseline. The
`Allocated` column is identical to the byte on both sides for all four trace rows, so the
`SearchTrace` cost pinned above is untouched.

**How much of the gain is the loop inversion, and how much is the index restructuring?** Answered
above, in the 2×2 table that follows the crossover: the restructuring is neutral on the path it does
not accelerate, and the loop is the whole of the win.

**Why the tail rows are flat.** The pass clears a buffer sized to the corpus before it scores
anything, so it has a fixed cost the per-document loop does not have. `ScoringPathBenchmarks`
measures the crossover by running both loops over the same query through the public surface — a
metadata filter that passes every document is one of the two conditions that send a query down the
per-document loop, and it changes nothing else (ShortRun, so read the direction, not the last
digit):

| Target df | Term-at-a-time | Per-document | Ratio |
|----------:|---------------:|-------------:|------:|
|         1 |        355.6 ns |      357.0 ns |  1.00× |
|         4 |        645.8 ns |      666.2 ns |  0.97× |
|        16 |      1,402.7 ns |    1,884.6 ns |  1.34× |
|        64 |      2,337.1 ns |    5,930.2 ns |  2.54× |
|       256 |      6,862.1 ns |   23,217.5 ns |  3.38× |
|     1,024 |     17,103.4 ns |  102,627.3 ns |  6.00× |
|     4,096 |     56,140.6 ns |  443,565.8 ns |  7.90× |
|     9,525 |    120,129.6 ns |  935,566.9 ns |  7.79× |

Read the direction, not the last digit: this is a ShortRun, and the per-document row's confidence
interval is tens of microseconds wide at the bottom of the table. The shape is what carries the
claim.

**And the same crossover, at three corpus sizes** — the question the table above cannot answer,
because it only ever measured 10,000 documents. Zipf corpora of 10,000 / 100,000 / 1,000,000
documents, streamed so the document array is never resident alongside the index, min of N in one
process, both loops over the same query:

| corpus | resident | fixed cost | per posting entry | per candidate scored | break-even | threshold |
|------:|---------:|-----------:|------------------:|---------------------:|-----------:|----------:|
|   10,000 |   ~41 MB |    0.5 µs |           12.8 ns |                96 ns |      ≈ 6 |         8 |
|  100,000 |  725 MB |      1 µs |           11.5 ns |               245 ns |      ≈ 4 |         8 |
| 1,000,000 | 7,083 MB |   16.1 µs |           12.6 ns |               295 ns |     ≈ 57 |        50 |

**Correction, and the `fixed cost` column is the shaky one.** The pass opens with an
`Array.Clear` of the whole ordinal space — `ScoreAccumulator.Rent(index.OrdinalSpace)` — so
part of that fixed cost is a `memset` whose length is the corpus. Timed on its own, on a
pooled array of that size (best of 7 rounds of 400, this host):

| ordinal space | `Array.Clear`, measured alone | against the `fixed cost` above |
|---------------:|------------------------------:|--------------------------------:|
|         10,000 |                      196 ns |                            0.5 µs |
|        100,000 |                     2.35 µs |                              1 µs |
|       1,000,000 |                    18.5 µs |                           16.1 µs |

At 10,000 documents the two are consistent: a 196 ns clear sits inside a 0.5 µs fixed cost.
At 100,000 the clear alone is **2.3× the whole recorded fixed cost**, and at 1,000,000 it
exceeds it. So the column cannot be read as an end-to-end intercept at the two larger sizes,
and this re-measurement does not say which number is wrong — the clear here is timed in a
tight loop with the array hot in cache, which is the friendliest possible condition for it.

A second reason to distrust the column: the measurements behind it are **not affine in
document frequency**, so an intercept is not well defined in the first place. Re-running the
100,000-document probe and fitting its own output (the `df = 2` row excluded, because the
threshold there is 8 and the engine takes the per-document path at `df = 2`) gives a
worst-case residual of **21.9 µs** on the term-at-a-time row and an intercept of **−122 µs**
on the per-document row. What *does* survive the re-run are the two slopes — 11.6 ns per
posting entry and 255 ns per candidate scored, against the 11.5 and 245 recorded — because
a slope is what a wide-range fit can still get right when the intercept cannot. The `break-even`
column inherits the same weakness, since it is read off the crossing of two fitted lines.

**What this section does not claim to have verified:** the 10,000 and 1,000,000 rows of the
table above. Only 100,000 documents was re-measured, because the probe needs roughly 11 GB
and the host had 7 GB free.

Two results here, and the second one is the more useful:

- **The new pass's per-entry cost is flat** — 12.8 / 11.5 / 12.6 ns across two orders of magnitude —
  while the old loop's per-candidate cost *triples*, 96 → 245 → 295 ns, as the postings
  dictionaries outgrow the cache. That is the mechanism behind the whole section: the win grows
  with corpus size instead of being a constant, and it is a cache effect, not an algorithmic one.
- **The break-even barely moves** (6 / 4 / 57), so the fixed cost is *not* what should set the
  threshold. Scaling the threshold on the fixed cost alone gives 9 / 97 / 976 and hands back a
  **1.2-2.4× win at two of the three scales**. The divisor is 20,000, fitted to the three measured
  crossings, which gives 8 / 8 / 50.

At 1,000,000 documents the effect is unmistakable, and this is the scale where the change stops
being a throughput win and becomes a latency one — the 38-word and Zipf tables at the top of this
section are both 10,000-document numbers, where the whole search already fitted in a millisecond:

| 1,000,000 documents, Zipf | term-at-a-time | per-document | ratio |
|---|---:|---:|---:|
| one term, df 70 |       28 µs |      25 µs |  0.91× |
| one term, df 256 |       44 µs |      89 µs |  2.01× |
| one term, df 1,000 |    141 µs |     356 µs |  2.52× |
| one term, df 10,035 |   309 µs |   3,898 µs | 12.60× |
| one term, df 99,254 | 1,998 µs |  37,426 µs | 18.73× |
| one term, df 496,842 | 6,064 µs | 145,827 µs | 24.05× |
| **three mixed terms** | **11,215 µs** | **328,717 µs** | **29.3×** |

The three-mixed-terms row is the shape a real query has, and **11.2 ms against 328.7 ms** is the
number that decides the question this section opened with: at a million documents the old path
crosses into latency a user can feel, and the new one does not.

Two honest limits on the 1,000,000 row. The vocabulary is held at 30,000 terms while the corpus
grows, so the tail gets *thicker* with scale — the rarest term is in 70 documents, not 1 — and a
longer tail at the same corpus size is not what was measured. And below df ≈ 57 the two paths are
within 10 % of each other, which is why the threshold is set where it is rather than at 1.

**What is still not measured:** anything above a million documents. Extrapolating the fixed cost
alone would put a 10-million-document crossing near 400, and the divisor encodes that direction —
but at that size the per-candidate cost is the quantity with the least evidence behind it, and it
is the one that sets the crossing.

**How much of the gain is the loop inversion, and how much is the index restructuring?** The
restructuring is worth separating, because it is not a free enabler. All three available cells,
same corpus, `ScoringPathBenchmarks` in both builds so the per-document row is the same query on
each side:

| df 9,525, 1 term | old index | new index |
|---|---:|---:|
| **document-at-a-time** |   910,206 ns | 935,567 ns (1.03×, i.e. noise) |
| **term-at-a-time** | did not exist | 120,130 ns |

So the restructuring is **neutral** on the path it does not accelerate, and the term-at-a-time pass
is **7.8×** against that same path on the same index — which means essentially the whole 7.6×
end-to-end is the loop, not the storage.

That 1.03× is worth a note, because a version of this measurement read 1.31× *slower*. The per-document
row was allocating 321 KB per search, 288 KB of it a per-candidate enumerator (the `PassesFilters`
defect below), and the resulting garbage-collection pressure was being charged to the loop as if it
were work. **A time number measured on a path that allocates a quarter of a megabyte per call is a
measurement of the allocator**, and "the restructuring costs 31%" should be read as "the restructuring
exposed an allocation bug".

**A pre-existing allocation bug, found by that measurement.** `SearchOptions.PassesFilters` is called
once per candidate document — on a filtered full scan, once per document in the corpus. It walked the
filter list with a `foreach` over an `IReadOnlyList<MetadataFilter>`, so the enumerator was resolved
*through the interface* and heap-allocated on every call. The comment sitting next to that loop
explained that a LINQ `All()` had been avoided because it would allocate an enumerator per candidate;
the `foreach` it annotated allocated one per candidate anyway. Measured on a 10,000-document corpus
with one filter that rejects nothing: **288,072 bytes per search before, 72 after** — and the same
search without a filter allocates 1,224. This is not caused by the scoring work and is present in the
pre-change build. `FilteredSearchAllocationTests` pins it with an allocation assertion (reverting the
loop makes it fail at 317,587 bytes per query) rather than a stopwatch, because that is the property
that regressed.

**What it costs.** Index building is at parity (`IndexBenchmarks`, before / after on this host):

| Method                            | DocumentCount | Before      | After       | Allocated before / after |
|-----------------------------------|--------------:|------------:|------------:|-------------------------:|
| BuildInvertedIndex                |          1000 |   11.73 ms |   11.98 ms |  9.37 MB / 9.37 MB |
| BuildIndexWithStopWordsAndStemmer |          1000 |   12.51 ms |   12.39 ms |  9.41 MB / 9.41 MB |
| BuildInvertedIndex                |         10000 |  164.99 ms |  162.90 ms | 91.08 MB / 91.08 MB |
| BuildIndexWithStopWordsAndStemmer |         10000 |  172.25 ms |  169.45 ms | 91.48 MB / 91.48 MB |

Resident memory of a served 10,000-document index, measured with `GC.GetTotalMemory` around an
index build (the caller's document array is the same on both sides and is outside the delta):

| Corpus / state               | Before   | After    |
|------------------------------|---------:|---------:|
| 38 words, untouched          |  40.9 MB |  40.9 MB |
| 38 words, one query served   |  40.9 MB |  41.2 MB |
| Zipf, untouched              |  72.5 MB |  72.7 MB |
| Zipf, one query served       |  72.5 MB |  72.9 MB |
| Zipf, three head terms       |  72.5 MB |  72.9 MB |
| Zipf, 200 mid terms          |  72.5 MB |  72.9 MB |

That is the dense ordinals the documents carry, plus the flat copy of the posting lists the
term-at-a-time pass walks: 8 bytes per posting entry against roughly eighty for the dictionary
entry, the `List<int>` and its backing array it mirrors. **Nothing is claimed about a corpus where
that ratio differs**, and the last row is the honest statement of what a build-then-never-query
index costs: nothing at all.

**A contiguous posting layout was the larger of the two wins, and measurement found it rather than
reasoning.** The first working version walked the dictionary in the term-at-a-time pass and landed
at 1,208 µs for three head terms (26,525 posting entries) where the identical arithmetic over
contiguous arrays landed at 122 µs — a 10× gap, all of it pointer chasing, because the enumerator
has to follow a `List<int>` and a document object per entry. The flat copy is built on first use
and rebuilt when the corpus changes, so a read-only index pays the build once while an index
mutated between every query pays an O(df·log df) build per query. The dictionary stays
authoritative; the flat copy is derived and can be dropped at any time.

#### The 20× is real, and it sits in a regime real queries never enter

A CSR-posting-list prototype into a score accumulator indexed by integer document ordinal measures
**0.097 ms against 2.00 ms** for a 2-term query over 10,000 documents. That ratio holds in the one
regime where the engine deliberately does not use candidate generation: the 38-word benchmark corpus
puts every term in ~74 % of documents, so `sum(df)/Count < 0.5` fails and the engine full-scans. On a
Zipf corpus — where the candidate path is the one real queries take — the engine sits at
0.001–0.013 ms, and there are microseconds left to win. A three-term tail query there has a `df` sum
of 2; a three-term head query has 9,628. Parity is not the obstacle: the prototype's scores come back
bit-identical to the engine's.

So the 20× is a ratio against a baseline that mostly does not occur — the « tuned vs tuned » mistake in
a different costume, one side measured in a regime the other would never be in. What it establishes is
that the head terms are where the money is, which the shipped tables above confirm. This is also why
the accumulation path is gated on a `df` threshold at all: a speedup measured inside the one regime
that bypasses candidate generation would never have suggested one. See the crossover table.

Two properties the shipped design carries, both of which the shape above forced:

- The accumulator is 8 bytes × document count **per concurrent query** — 80 KB at 10,000 documents,
  8 MB at a million. It is rented from `ArrayPool` per search and returned, so a steady stream of
  searches allocates nothing after the first few; the memory tables above are the measured
  consequence, not a projection.
- The flat arrays carry ordinals and frequencies only. `GetTermPositions` therefore stays a dictionary
  lookup, which the phrase gate and `ProximityReranker` call per (document, phrase term); the
  pointer-chasing walk remains in the write path. Replacing the authoritative
  `Dictionary<string, List<int>>` is a larger change than this one and is not attempted.

#### The fold's cost is the scatter, not its arithmetic

A posting entry costs ~13 ns at the head, and the divisions in it are not what fills that time. BM25
evaluates one division per entry for the length normalization — `1 − b + b·dl/avgdl`, identical for
every document of the same length — and a second for the saturation. Replacing the first with a
length-keyed table built once per query measures **0.1980 ms against 0.1984 ms** on a query that
folds 9 525 entries: seven launches of each build alternated in one loop, on minima that vary by
2.5 % between launches of the same build. The table also costs 440 B per query, 65 % of the search's
steady-state allocation, so the division stays.

What the entry does cost is one dependent read of `lengths[ordinal]` and one write into
`_scores[ordinal]`, scattered across a corpus-sized buffer, plus the per-entry bookkeeping. The
out-of-order engine overlaps arithmetic with those reads, so an operation that is expensive in
isolation is free in context.

The same holds one level down, and the same measurement shape says so. The dense scan takes the
square root of a document's stored squared norm once per document per query; storing the norm itself
is bit-identical and removes 10 000 square roots per query, which measures inside the noise at 384
dimensions (2.71 against 2.66 ms over 10 000 documents, distributions overlapping) and inside it
again at 8 dimensions, where a document costs ~50 ns and the loop's branches and window insertion
dominate an eight-wide dot product.

So the distinction that decides these changes is not "arithmetic is cheap" but **how many times the
operand repeats**: an operation once per iteration is overlapped and invisible, an operation once per
*pair* — a norm recomputed for every (query token, document token) cosine — is multiplied by the
pair count and is where the time is.

## Scale lane (`scale`): index build and tail queries per corpus size

The search-side benchmark classes top out at 10,000 documents: `IndexBenchmarks`,
`CandidateSearchBenchmarks` and `ScoringPathBenchmarks` all build their corpus at that size (the one
larger corpus in the project is `StoreWriteBenchmarks`' 20,000-document write path), and the
crossover tables above contain 100,000- and 1,000,000-document rows that no class in the project
produces. The scale lane is the in-project command that builds a corpus at one or more of 10,000 /
100,000 / 1,000,000 documents and reports, per size, what the index cost to build and what a tail
query costs to serve.

It is a **direct run, not a BenchmarkDotNet report**. A BenchmarkDotNet job rebuilds its setup for
every invocation, so a 1,000,000-document index would pay the build on every launch and hold several
gigabytes while it did. The lane builds one index per size, once, in a process that does nothing
else. The measured rows in this section come from that mode — a `Stopwatch` in a plain process, with
the warm-up stated below and none of BenchmarkDotNet's outlier removal or confidence intervals — so
the absolute latencies are host-bound.

```bash
# the default lane: 10,000 and 100,000 documents
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- scale
# one size at a time; naming the size is the opt-in
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- scale 10000
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- scale 100000
# add 1,000,000 to the default set: by flag, or by environment variable
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- scale --include-1m
LEXISHARP_SCALE_INCLUDE_1M=1 dotnet run --project bench/LexiSharp.Benchmarks -c Release -- scale
```

A plain `dotnet run --project bench/LexiSharp.Benchmarks -c Release` still runs the BenchmarkDotNet
suite and nothing else, so the default invocation's cost is unchanged.

**What is reported, per size.**

- **Build** — one `RankedTextSearchEngine.Index` over the corpus, timed with a `Stopwatch`. A
  discarded 1,000-document build is run first so the reading is not the JIT's first touch; corpus
  generation is timed separately and excluded. It is a single build, not a mean.
- **Retained bytes** — `GC.GetTotalMemory` before and after the build, each reading preceded by two
  forced, blocking, compacting collections, divided by the document count. This is a **managed-heap**
  measurement: it does not count native or runtime memory (JIT code, thread stacks, the GC's own
  structures), page cache, or the corpus's `SearchDocument` objects and text — those are alive on
  both sides of the reading and therefore outside the delta. The `after queries` reading is taken
  again once the tail queries have run, which prices what a first search retains: the score
  accumulator rented from `ArrayPool` (8 + 1 + 4 bytes per ordinal number, returned to the pool
  rather than to the GC) and the flat posting copy of each queried term. At 10,000 documents that
  second reading did not move at the printed precision; at 100,000 it is 1.6 MB, consistent with the
  pooled accumulator's three arrays alone (1.63 MB at that ordinal space).
- **Query latency** — one tail term (`w20000x`) and three tail terms (`w20000x w25000x w29000x`),
  the same tail terms `CandidateSearchBenchmarks` uses, at `Limit: 10`. Each query is warmed for five
  seconds of wall time, then timed for 1,000 / 300 / 100 searches at 10,000 / 100,000 / 1,000,000
  documents; `min`, `p50` and `mean` are over individual searches in one process. The row prints the
  warm-up's search count and the cost of its last 100 ms beside the measured `p50`; the measurement
  below is why.
- **Allocation per search** — `GC.GetAllocatedBytesForCurrentThread` over a separate loop of the
  same length, divided by the iteration count, after the warm-up. It includes the result list the
  caller receives, so it is what a caller pays for that search.

The corpus is `CorpusFactory.CreateZipfDocuments` (Zipf exponent 1.07, 50 words per document, a
30,000-term vocabulary), the default tokenizer and `Bm25Scorer` — the same factory the 10,000-document
classes above use, and the query terms `CandidateSearchBenchmarks` uses, so a 10,000-document row
here and a row there describe the same corpus and the same queries. The lane prints each query's summed document frequency at every size, because the
vocabulary is held fixed while the corpus grows: a tail term is in 1 document at 10,000 and in 9 at
100,000, and the printed `df` is what keeps that visible.

**Measured on 2026-10-25**, three runs of `scale` (the default 10,000 and 100,000 sizes), on
Manjaro Linux, Intel Core i7-8850H (12 logical processors), .NET 10.0.13, `ServerGC=True`, 15 GiB
of RAM. The machine was not dedicated to this lane: other sessions were building on it. The build
column lists the readings in run order; latency is the median of the three run `p50`s with the
min–max of those `p50`s beside it. The retained figures and the allocation counts printed the same
values in all three runs.

| Documents | Build (3 runs) | Retained after build | B/document | Retained after queries | One tail term | p50 | Alloc/search | Three tail terms | p50 | Alloc/search |
|---:|---|---:|---:|---:|---|---:|---:|---|---:|---:|
| 10,000 | 665 / 638 / 655 ms | 76.9 MB | 8,063 | 76.9 MB | df 1, 1 hit | 0.40 µs (0.40) | 840 B | df sum 2, 2 hits | 0.90 µs (0.90–1.50) | 1,160 B |
| 100,000 | 5.04 / 4.86 / 4.82 s | 661.5 MB | 6,936 | 663.1 MB | df 9, 9 hits | 3.70 µs (3.60–3.80) | 648 B | df sum 29, 10 hits | 7.10 µs (4.40–8.20) | 688 B |
| 500,000 | 25.05 s (one run) | 3,224.0 MB | 6,761 | 3,224.0 MB | df 90, 10 hits | 13.20 µs | 672 B | df sum 220, 10 hits | 19.80 µs | 688 B |
| 1,000,000 | 53.93 s (one run) | 6,483.0 MB | 6,798 | 6,483.0 MB | df 152, 10 hits | 6.20 µs | 856 B | df sum 389, 10 hits | 12.50 µs | 1,008 B |

The 500,000- and 1,000,000-document rows are each a **single run**, made after the three above, on
the same host; their build columns are one reading rather than three, so they carry no spread. The
million-document run was previously impossible on this machine — at ~6.8 KB of managed heap per
document the index needs about 6.5 GB beside the corpus's own documents, and the host had 7.4 GB
available the first time it was tried. Clearing the build output of twenty worktrees freed enough for
it to fit: **6,483.0 MB retained, 53.93 s to build, 8.9 GB available at the start**.

**The 1,000,000 row is not the same pass as the three smaller ones.** It was measured after the
cursor pass landed, so its latency columns read the document-at-a-time traversal (6.20 / 12.50 µs);
the 10,000 / 100,000 / 500,000 rows predate it and price the accumulation pass. The cursor paragraph
below gives the current build's figures for those sizes; comparing the million-document row with
them directly would compare two traversals.

`hits` is the number of results the search returned at `Limit: 10`; the three-term query at
100,000 documents has 29 posting entries and returns a full page. The per-document retention reading
falls with size — 8,063 B at 10,000, 6,936 at 100,000, 6,761 at 500,000 — and the three points are a
direction in this lane's readings, not a cost model. The tail term thickens with the corpus while the
vocabulary is held at 30,000 words, which is why the `df` column matters: the one-term query costs
0.40 / 3.70 / 13.20 µs at df 1 / 9 / 90, so the growth is in the posting entries the query has to
read and not in the corpus size alone. The per-search allocation is also lower at the larger sizes
for the same query; the lane does not decompose it, and no cause is claimed here. The three-term
`p50` at 100,000 spans 4.40–8.20 µs across the three runs, which is this host's run-to-run spread,
not a property of the query.

**The two latency columns price the term-at-a-time accumulation pass.** A sparse query takes the
document-at-a-time cursor pass, which removes the corpus-sized buffer and its clear; the same queries
on the current build read 1.00 / 2.20 µs at 100,000 and 4.4–4.5 / 7.9–8.8 µs at 500,000 in the
[cursor lane](#cursor-lane-cursor-a-tail-querys-p50-p99-and-allocation) below and the scale lane's own
run of that build.

The build readings span 638–665 ms at 10,000 documents and 4.82–5.04 s at 100,000. Read the column
as a range on this host, not as a scaling factor: the machine was shared, and the lane times one
build per size rather than a mean.

**The same query through BenchmarkDotNet.** `CandidateSearchBenchmarks.OneTerm` issues the same
call on the same corpus. On this tree and host,
`--filter "*CandidateSearchBenchmarks.OneTerm" --job short` reports **537.2 ns mean, 522.3 ns min,
840 B allocated**, with a 99.9 % confidence-interval half-width of 416.7 ns on a host that was also
running other sessions' builds. The lane's 10,000-document row for that query reads **0.40 µs
`p50`, 0.45–0.55 µs mean, 840 B** across the three measured runs — the same allocation. A fourth
run of the same binary read 0.80 µs `p50` / 0.82 µs mean, so this host's spread on that row is
0.40–0.80 µs.

The warm-up is not politeness. Warming the query with 100 calls instead reads **2.2 µs** for this
row — 4.1× the BenchmarkDotNet mean, with byte-identical allocation, which is what shows the two
readings are the same code path. Timed in 100 ms windows on this host, the query costs ~3.5 µs per
search for the first 0.4 s, ~1.3 µs at half a second, and ~0.6 µs from 0.7 s onward; the same
binary with `DOTNET_TieredCompilation=0` reads 0.6 µs after the same 100-search warm-up. The extra
time is tiered compilation running its tier-1 instrumented code — not a different code path, and
not GC pacing: BenchmarkDotNet's process reports `GC = Concurrent Server` and the lane prints
`ServerGC=True`, so neither is workstation, and the lane's per-iteration clock reads cost a few
nanoseconds (mean 2–8 ns over 10,000 empty iterations). Each query is warmed for five seconds, and
the last 100 ms of that warm-up is printed with the row so that a run whose code has not settled is
visible rather than silent. The warm-up adds 20 s to the default two-size run; it is the difference
between measuring the search and measuring the JIT.

**The lane reaches 1,000,000 documents**, at about 6.5 GB of managed heap, and the row is in the
table above; it was taken once, on a host with 8.9 GB available. The 10,000 / 100,000 / 1,000,000
crossover figures elsewhere on this page are still the ad-hoc stopwatch harness's measurement, not
this lane's.

**What else this section does not establish.** Only the default tokenizer, BM25 and `Limit: 10` were
measured; head terms, other analyzers, filtered queries, concurrency, and a distribution beyond
`min` / `p50` / `mean` are all outside the lane. The retained figure is managed heap only and
excludes everything listed above, so it is not comparable with an RSS reading.

## Pruning headroom lane (`wand`): the posting entries a block bound can skip

A block-max WAND or MaxScore traversal pays only where it can skip posting entries the
term-at-a-time pass reads and scores. The lane measures that share on the same Zipf corpora and two
of the same tail queries the scale lane prices, plus a head term, so the question "is pruning worth
implementing here" is answered by a count rather than by the shape of the format.

```bash
# 100,000 documents
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- wand 100000
# 500,000 documents: about 3.2 GB of managed heap, so the size is named explicitly
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- wand 500000
```

**What it computes.** One index per size, from `CorpusFactory.CreateZipfDocuments` exactly as the
scale lane builds it. Each query term's postings are read out of the index's flat arrays (ordinals
ascending, term frequencies, document lengths), cut into the 128-ordinal windows `PostingBlocks`
writes (`ordinal / BlockSpan`, the same alignment, which is what makes the windows of different terms
summable), and each window is given the bound its largest term frequency and shortest document imply:

```
idf · maxTf · (k1 + 1) / (maxTf + k1 · (1 − b + b · minLen / avgdl))
```

That is the pair the block header stores — `maxFrequency` and `minDocumentLength` — evaluated through
BM25's monotone contribution, so it is a **true block bound**, not a term-level maximum. The lane
checks every entry against its own window's bound while building it. It also runs a term-level
variant, because the two have different costs: a global maximum can rule out *scoring* a document but
cannot skip the entry that would have to be read to know the document is there.

The traversal is document-at-a-time over those arrays with a top-10 window it fills itself. Let `d`
be the smallest current ordinal and `w` the earliest window end among the terms' current windows.
For a document `x` in `[d, w]`, a term whose cursor is at or below `x` has `x` inside its current
window, and a term whose cursor is above `x` does not hold `x` at all — so the sum of the current
window bounds bounds every document in the range. When that sum is below the threshold, every cursor
jumps past `w` and those entries are never read. A second test applies the same bound to the single
document at `d`, and a third variant uses the term-level maxima. No essential/non-essential ordering
is needed: the cursors say exactly which terms hold the document at `d`, so the bound is taken over
the terms that can contribute rather than over a prefix chosen by maximum, which is the refinement
MaxScore's ordering exists to approximate. Every final page is compared with the
engine's own answer, id for id and score bit for score; all of the compared pages match exactly.

**This is a lower bound, not a faithful BMW.** A real block-max WAND uses its pivot to advance over
documents this traversal still scores, so the skipped fractions here are a lower bound on what WAND
would skip. The threshold is grown from the window the traversal itself fills, never taken from the
exhaustive pass, so the count is not an oracle's.

**Where the bound comes from, and what is not reachable.** `InMemoryTextIndex` — the index the scale
lane measures — stores flat posting arrays and no blocks, so this lane computes the bounds from those
arrays. A segment written by `PostingBlocks` already carries the same bound in every block header,
and `PostingBlockReader` exposes it as `MaxFrequency` and `MinDocumentLength`, so a segment built
from this corpus carries the same bound over the same windows. This lane traverses the in-memory
postings, not a segment, and does not measure the segment path. The *cost* of
obtaining the bound does not transfer, and this lane measures neither side of it: a segment pays it
once, at write time; an in-memory pass would need a per-window array built at index time or a
per-query walk of the postings, and the latter is the work pruning is meant to avoid.
`PostingBlockReader` is internal to `LexiSharp`, and the benchmark project has no access to it, so no
production accessor was added or needed for this measurement.

**Measured on 2026-10-26**, Manjaro Linux, Intel Core i7-8850H (12 logical processors), .NET 10.0.13,
`ServerGC=True`, 15 GiB of RAM. The host was shared with another session's build and test runs, which
is why the lane prints the load average beside each size; the entry counts are deterministic and did
not move across runs, and the latencies did. The table below is one run of both sizes (load 2.9 at
the 100,000-document build, 3.4 at the 500,000-document build). The quietest readings of the same
tree were 3.40 / 4.10 µs and 1.19 ms at 100,000 documents and 13.10 / 19.30 µs and 5.40 ms at
500,000; a run at load 7–9 read 22.9 / 27.0 µs and 11.2 ms on identical counts.

| Documents | Query | df | Postings | Read | Skipped by the block bound | Skipped | Contributions computed | Ceiling, reads | Ceiling, weight | Contributions under the term-level bound |
|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 100,000 | `w20000x` | 9 | 9 | 9 | 0 | 0.0 % | 9 | 1.00× | 1.00× | 9 |
| 100,000 | `w20000x w25000x w29000x` | 29 | 29 | 29 | 0 | 0.0 % | 29 | 1.00× | 1.00× | 29 |
| 100,000 | `w1x` | 95,402 | 95,402 | 11,503 | 83,899 | 87.9 % | 11,503 | 8.29× | 8.29× | 95,402 |
| 500,000 | `w20000x` | 90 | 90 | 90 | 0 | 0.0 % | 90 | 1.00× | 1.00× | 90 |
| 500,000 | `w20000x w25000x w29000x` | 220 | 220 | 219 | 1 | 0.5 % | 64 | 1.00× | 3.44× | 64 |
| 500,000 | `w1x` | 477,361 | 477,361 | 16,719 | 460,642 | 96.5 % | 16,719 | 28.55× | 28.55× | 477,361 |

`df` is the summed document frequency of the query terms; `Read` is posting entries the traversal
touches, which is postings minus `Skipped`; `Contributions computed` is the entries whose BM25 weight
is evaluated, which for the block bound is the same as `Read` except on the three-term row, where 155
touched entries were cleared by the per-document test. The two ceilings divide the posting count by
`Read` and by `Contributions computed`: they are the speedup a traversal would get **if a posting
entry cost nothing but its read and its weight, and the traversal's bookkeeping and heap were free**.
They are not latency predictions.

The same queries, timed through `RankedTextSearchEngine.Search` at `Limit: 10` in the same process,
warmed for five seconds the way the scale lane warms them:

| Documents | Query | p50 | Postings at 12.6 ns/entry | Isolated accumulator clear |
|---:|---|---:|---:|---:|
| 100,000 | `w20000x` | 3.50 µs | 3.2 % | 2.3 µs (66 %) |
| 100,000 | `w20000x w25000x w29000x` | 4.10 µs | 8.9 % | 2.3 µs (56 %) |
| 100,000 | `w1x` | 1.23 ms | 98 % | 2.3 µs (0.2 %) |
| 500,000 | `w20000x` | 13.50 µs | 8.4 % | 9.6 µs (71 %) |
| 500,000 | `w20000x w25000x w29000x` | 21.40 µs | 13.0 % | 9.6 µs (45 %) |
| 500,000 | `w1x` | 6.42 ms | 94 % | 9.6 µs (0.1 %) |

The 12.6 ns per entry is the figure the repository carries from an earlier session at 1,000,000
documents; it is not re-measured here. Across these runs the head row implies **11.3–13.4 ns** per
entry (5.40–6.42 ms over 477,361 entries), so the share is the right order of magnitude and not a
precise fraction. The clear column is an isolated `Array.Clear` of the ordinal-space-sized `byte[]`
that `ScoreAccumulator.Rent` hands the accumulating pass, best of seven; it is a bracket, not the
clear's marginal cost inside a search.

**What the numbers say.** On the two queries the scale lane prices at 500,000 documents, a block
bound skips **0.0 %** and **0.5 %** of the posting entries. The reason is visible in the lane's own
density column: the rare terms occupy about one entry per 128-ordinal window (96.6 % and 100 % of
their windows hold a single entry), so there is nothing inside a window to clear; and in the
one-term query every one of the 90 candidates has the same score, because the term occurs once in
each of them, so no document is strictly below tenth place and no bound that respects the id
tie-break may skip one. The term-level bound does better on the three-term query — 64 of 220
contributions, a 3.44× ceiling on the weight arithmetic — but it reads every entry: a MaxScore pass
would save arithmetic, not reads.

The third query is a single head term, where windows hold 122 entries and the bound skips **96.5 %**
of the postings. That is the one place in this lane with a real ceiling: **28.55×** on the postings
and the weight, at a measured p50 of **5.40–6.42 ms**. The 477,361 entries account for essentially
the whole of that p50, so the pass is postings-bound and the bound has something to win. Whether it is
worth W7 depends on a question this lane does not answer — how much of real traffic is head-term
queries — and the scale-lane table, which is the published tail-latency row, contains neither this
query nor any other head term.

**The fixed cost pruning cannot touch, and the one it could.** Every accumulating search rents and
clears a byte array as long as the ordinal space: 9.1–9.6 µs isolated at 500,000 documents, 69–71 %
and 45–47 % of the two tail queries' p50 across the runs above, against postings shares of 8.4–8.7 %
and 13.0–14.4 %. A pruning pass keeps that clear. A document-at-a-time traversal — which is what WAND and MaxScore are — would not need the
corpus-sized accumulator at all, because it sums one document at a time and keeps only the page
window; but that is a property of changing the traversal, not of the bound, and this lane does not
measure it. For the tail rows the traversal is the larger question, and the bound is not.

**What is not measured.** The traversal's bookkeeping — pivot and cursor comparisons, the binary
searches a skip performs, heap updates, allocation — so neither ceiling is an end-to-end prediction.
The cost of building the bounds, in memory or in a segment. The segment and store index paths: only
`InMemoryTextIndex` is measured. P99, concurrency, and query mixing. Queries with phrases, filters,
expansions or several head terms. Other analyzers, `k1`, `b`, `Limit`, or corpus sizes. Document
lengths are exactly 50 tokens for every document in this corpus, so the length half of the bound is a
constant and the bound is as tight as the term-frequency distribution allows; a corpus with variable
lengths gives a looser bound and a lower skipped share, so the 96.5 % head figure is not an estimate
for another corpus. The one-term tail's 0.0 % holds for the queries this lane runs, whose rare term
occurs at most once per document; a tail term with repeated occurrences in some documents does have a
threshold above its common contribution and can be pruned.

## Cursor lane (`cursor`): a tail query's p50, p99 and allocation

The accumulating pass walks the posting lists term at a time and folds them into a buffer as long as
the ordinal space. That buffer is rented from `ArrayPool` and its recorded-flags array is cleared
before every search, so the search pays a cost proportional to the **corpus** before it reads a
posting. The alternative is a document-at-a-time traversal: one cursor per query term, the smallest
current ordinal is scored, and the score goes straight into the top-`k` window — no corpus-sized
buffer, and nothing to clear.

```bash
# 10,000 and 100,000 documents, the scale lane's default sizes
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- cursor
# one size at a time; 500,000 needs about 3.2 GB of managed heap
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- cursor 500000
# add one query per document-frequency target, which is how the density threshold was fitted
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- cursor 100000 --sweep
```

**What it measures.** The same corpus, the same three queries and the same five-second warm-up as
the scale lane: one tail term (`w20000x`), three tail terms (`w20000x w25000x w29000x`) and one head
term (`w1x`), at `Limit: 10`. Each query reports `min`, `p50`, `p99` and `mean` over 1,000 / 300 /
100 individual searches at 10,000 / 100,000 / 500,000 documents, the allocation per search from
`GC.GetAllocatedBytesForCurrentThread` over a separate loop of the same length, and the isolated
`Array.Clear` of an ordinal-space-sized `byte[]` beside it. It is a `Stopwatch` in a plain process,
not a BenchmarkDotNet report.

**How the cursor pass decides.** It is not a threshold guess: the pass removes a fixed cost that
grows with the corpus and adds a per-document cost that grows with the query's reach, so it wins on
sparse queries and loses on dense ones. The engine takes it when the query's summed document
frequency is at most `ordinalSpace / 200` **and** the index can lend its posting arrays as arrays
(`IPostingArrayIndex`; `InMemoryTextIndex` always can, and a committed store can when it was opened
with `decodedCacheEntries`). An index that cannot — a store without the decoded-postings cache, an
expanding or context-enriching decorator, a multi-segment chain with an overlay — keeps the
accumulator; the pass declines and nothing changes. `--sweep` measures the crossing: one term near
each document frequency in `10, 25, 50, 250, 500, 1,000, 2,500, 5,000, 20,000`, the cursor pass
forced on every query so the losing side of the crossing is visible too.

**Measured on 2026-10-26**, Manjaro Linux, Intel Core i7-8850H (12 logical processors), .NET 10.0.13,
`ServerGC=True`, 15 GiB of RAM, one index per size in its own process, **on a quiet host**: the
one-minute load was 0.5–1.5 before every run and no process other than the lane held a core. The
builds were run **alternating** — baseline, after, after, baseline — so each side is two runs and the
latency columns are their mean; `p50` is the lane's own, no BenchmarkDotNet outlier removal. The lane
prints, per row, which pass the engine's own decision selects (`per-document`, `accumulator` or
`cursor`) and asserts it against the allocation that pass adds: the same query is run through an
engine forced to each accumulating pass, and the default engine's bytes per search must equal the one
the label names. A cursor row's clear share prints a dash, because it rents no accumulator to clear.
The before column is the library at `3eedf42` — the tree the lane commit sits on, before the cursor
pass existed; the after column is the tree this page describes. The 1,000,000 rows are the exception
and are marked as such below: both their columns are forced passes on this build.

| Documents | Query | df sum | Pass | Before p50 | After p50 | Before p99 | After p99 | Before alloc | After alloc |
|---:|---|---:|---|---:|---:|---:|---:|---:|---:|
| 10,000 | one tail term | 1 | per-document | 0.40 µs | 0.40 µs | — | — | 840 B | 840 B |
| 10,000 | three tail terms | 2 | per-document | 1.00 µs | 1.00 µs | — | — | 1,160 B | 1,160 B |
| 10,000 | one head term | 9,525 | accumulator | 131.5 µs | 134.5 µs | — | — | 672 B | 672 B |
| 100,000 | one tail term | 9 | cursor | 3.35 µs | **1.00 µs** | 4.40 µs | 2.30 µs | 648 B | 832 B |
| 100,000 | three tail terms | 29 | cursor | 4.10 µs | **2.20 µs** | 4.95 µs | 2.85 µs | 688 B | 1,008 B |
| 100,000 | one head term | 95,402 | accumulator | 1.24 ms | 1.28 ms | 1.42 ms | 1.38 ms | 672 B | 672 B |
| 500,000 | one tail term | 90 | cursor | 13.15 µs | **4.40 µs** | 15.15 µs | 5.05 µs | 672 B | 856 B |
| 500,000 | three tail terms | 220 | cursor | 19.85 µs | **7.85 µs** | 29.85 µs | 14.00 µs | 688 B | 1,008 B |
| 500,000 | one head term | 477,361 | accumulator | 5.19 ms | 5.24 ms | 5.75 ms | 5.49 ms | 672 B | 672 B |
| 1,000,000 | one tail term | 152 | cursor | 24.60 µs | **6.35 µs** | — | 9.40 µs | 672 B | 856 B |
| 1,000,000 | three tail terms | 389 | cursor | 36.15 µs | **12.50 µs** | — | 23.95 µs | 688 B | 1,008 B |
| 1,000,000 | one head term | 954,943 | accumulator | — | 10.78 ms | — | 11.38 ms | 672 B | 672 B |

**The 1,000,000 rows are not a checkout of `3eedf42`.** They are two runs of this build on
2026-10-29, with the accumulator forced for the before column and the cursor pass forced for the
after one, alternated inside one loop, and each cell is the mean of the two (the spread was
6.30–6.40 µs, 12.30–12.70 µs, and 10.05–11.51 ms for the head). The before `p99` is not tabled
because the lane prints a `p50` for a forced pass and no distribution behind it. The pre-change build
took the accumulator for every one of these queries because the cursor pass did not exist, which is
what makes the forced reading its equivalent; the head row has no before column because both builds
take that pass there, so the pair would be the same code twice. A same-day control at 100,000
documents reproduced the published after column exactly on the three-term query (2.20 µs) and the head
row without inflation (1.18 ms against 1.24–1.28 published); its one-term row read a 1.00 µs minimum
and a 1.50 µs median, which is the resolution of a one-microsecond measurement rather than a shifted
pass. That row is left as measured rather than re-taken. **The two tail queries are denser here than
at 500,000** — df 152 and 389 against 90 and 220 — because the generated vocabulary is held at 30,000
terms while the corpus doubles, so part of the 3.9× and 2.9× this row shows over the 500,000 row's
2.99× and 2.53× is density rather than size.

The two 10,000-document tail rows are below the accumulation floor (8 posting entries), so both
builds take the per-document loop and the cursor pass was never in play; the head row is dense and
stays on the accumulator at every size. The 10,000-document p99s are not tabled: the head's moved
between 181.6 and 323.0 µs across runs whose `p50` stayed at 131–135 µs, which is the host rather
than the pass. **The head rows are therefore the same code path in both builds**, and the differences
there (1.24 → 1.28 ms at 100,000, 5.19 → 5.24 ms at 500,000) are not attributed: the allocation is
identical and no mechanism for them was established.

The isolated clear the cursor pass removes is **2.2–2.4 µs at 100,000, 9.0–9.4 µs at 500,000 and
18.9–19.9 µs at 1,000,000** — 69–71 % of the one-term `p50` and 47–56 % of the three-term one before
the change at 100,000 and 500,000, and **79 % and 54 %** at 1,000,000. After it, the tail `p50` is
below the isolated clear at every size — by 3.1× at 1,000,000, where the clear alone is longer than
the whole search — which is what a search that does not clear a corpus-sized buffer looks like. The
allocation rises by **184 B** on a one-term query and **320 B** on a three-term one: a weight per
term, a `PostingArrays` per term, a cursor array and the sink object — O(query) bytes, against a
corpus-sized buffer that is rented (and was cleared) before.

**The host is part of the measurement.** A run of the same after build while another session's SPLADE
encoder held about 590 % CPU (load 7.6) read **1.80 / 6.10 µs** at 100,000 with the cursor pass's
allocation (832 / 1,008 B) against 1.00 / 2.20 µs on the quiet host. The pass did not change — the
corpus and the reach decide it, not the load — the latency did, by 1.8× and 2.8×. That is why the
table records the load it was taken at and why the lane asserts and prints the pass: a contended run
and a different traversal look the same in a `p50` column and not in an allocation column.

**The density crossing, measured with `--sweep`** (the baseline build against the build that carries
the cursor pass, alternating them; the shipped build takes the cursor pass only at or below the cap,
so the losing rows are the reason the cap exists):

| Documents | Last reach that won | Before → After | First reach that lost | Before → After | Cap (`df <= N/200`) |
|---:|---:|---|---:|---|---:|
| 25,000 | 250 | 12.10 → 11.60 µs | 1,005 | 36.50 → 38.70 µs | 125 |
| 50,000 | 250 | 11.30 → 9.90 µs | 5,014 | 144.60 → 107.30 µs | 250 |
| 100,000 | 250 | 11.00 → 10.00 µs | 1,002 | 45.00 → 48.80 µs | 500 |
| 500,000 | 1,000 | 57.90 → 50.60 µs | 5,006 | 143.80 → 155.00 µs | 2,500 |

The 10,000-document row is not in this table: it was measured again with both passes timed on one
build, and its df 50 point is a tie rather than a win, so it belongs to the interval below. The four
rows here have not been re-measured that way.

The 50,000 row's losing point is the doubtful one: the baseline's `p50` there was 144.6 µs against a
minimum of 82.0, while the cursor pass read 107.3 against 105.5, so the relative order of the medians
rests on a noisy baseline. **Not measured:** the exact crossing between each pair of points, and any
corpus size other than those four; the cap is set inside the interval at every measured size, not on
a fitted curve.

**Both passes on one build, alternated (`--both`).** The table above alternates two *builds*, so at a
row where both of them take the same pass, the difference between its two numbers is drift and not a
pass — which is what the 50,000-document row's note says in other words. `--both` times the
accumulator and the cursor pass on the same index, the same query and the same session, one of each
per iteration, so a scheduler hiccup or a collection moves all the readings together instead of
charging itself to one of them. Every reading below is three runs on 2026-10-28, on the host named
above at a one-minute load of 2.5-2.9; the rows above were taken at 0.5-1.5, which is part of why
their margin is larger than these.

| corpus | the cursor pass wins by | tie | the accumulator wins by |
|---|---|---|---|
| Zipf, 10,000 documents | 1.12× at df 10 | df 25 (1.05-1.10×) and df 50 (1.00-1.04×) | 1.09-1.11× at df 250, 1.18-1.32× from df 1,000 |
| ArguAna, 8,674 real documents | 1.40× at df 10, 1.08-1.17× at df 25 | df 50 (1.00-1.05×) | 1.13-1.18× at df 250, 1.17-1.28× from df 1,003 |
| TREC-COVID, 171,332 real documents | 8.67-10.20× at df 10, 3.39-3.65× at df 50, 1.23× at df 500 | df 1,000 (1.00-1.01×), df 2,500 (1.09×) | 1.13× at df 4,993, 1.14-1.18× at df 19,901 |

The crossing is an **interval, 25 < df < 250, and it is the same interval on both small corpora**, with
the tie at df 50. The rule caps at `ordinalSpace / 200` — 50 at 10,000 documents and 43 on ArguAna — so
it sits inside that interval on a corpus whose document lengths run from 0 to 1,014 tokens (p50 64,
mean 85.9, p95 268) where the synthetic one holds every document at exactly 50. **The divisor
therefore transfers, and nothing in the rule changed.** The 10,000-document row this table used to
carry read 2.80 → 2.50 µs at df 50, a 1.12× win for the cursor pass; the two-pass instrument reads
that point as a tie (cursor 2.70-2.80 µs against accumulator 2.70-2.90 µs over three runs), so the
row is withdrawn and replaced by the interval above.

**It moves up with the corpus, and the divisor moves with it.** TREC-COVID is 171,332 real biomedical
abstracts — lengths 0 to 18,964 tokens, p50 21, mean 105.5, p95 311 — over 211,708 terms, which is 20
times ArguAna's document count and 6 times its vocabulary. Its crossing is at df 1,000-2,500 rather
than 25-250: the interval rose by the same order as the corpus. The cap there is
`171,332 / 200 = 856`, which lands **on the tie**, so the engine gives up the cursor pass exactly where
the two cost the same and the most it forgoes is the 1.09× the accumulator gains at df 2,500. The
clear at this size is 4.6 µs — 21 % of a df 1,000 query's 21.8 µs — and because it is a fixed cost the
cursor pass reads **8.7-10.2× faster** on a ten-document query that costs 0.5 µs. Two runs at load
1.49-1.60, every row within 5 % of its twin.

What that does not say: it is one corpus at one size. TREC-COVID's lengths are hardly typical, a p50
of 21 tokens beside a maximum of 18,964; and the clear is one byte an ordinal, so at 500,000 and
1,000,000 documents the fixed cost is 3 to 4 times larger and the crossing would sit higher again.
The four build-alternating rows above are not re-measured, and the interval is wide everywhere: the
measurement says the cap is inside it, not that 200 is the best divisor in it.

```bash
# the real corpus, sweep only: a real vocabulary has no w1x, so the fixed shapes are skipped
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- cursor --corpus arguana --sweep --both
# the same at a size where the clear is material: fetched once, 74 MB, md5-pinned in the lane
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- cursor --corpus trec-covid --sweep --both
```



**Not the same answer as pruning.** Block-max WAND skips posting entries; this pass reads the ones the
crossing allows it to and changes the traversal instead. On the tail queries the pruning lane measures
0.0 % and 0.5 % skipped, so there is nothing for a bound to win there, and the head term's 96.5 %
skipped share is a different question. The two are complementary and neither subsumes the other.

**What is not measured.** Phrases, required/prohibited operators, field scopes and expansions: those
queries decline the accumulating pass entirely and keep the per-document loop, so they are untouched
here. *This* section measures the plain tail query and the head term at `Limit: 10`, with the default
BM25, `k1 = 1.5`, `b = 0.75`, the default tokenizer and the in-memory index. Filters and facets were
checked for parity and are not timed. The segment-backed store path was checked for parity (a
committed store opened with `decodedCacheEntries` lends its arrays; one without the cache falls back to
the accumulator and serves the same page) and is **not** timed. P99 under concurrency, query mixing,
other analyzers, other scorers, other `Limit` values and corpora above 1,000,000 documents are not
measured in this lane.

## Dense quantisation lane (`quantize`)

`InMemoryVectorSearchEngine` holds its vectors in one contiguous float32 block: 384 components is
1,536 B per document, or about 1.5 GB at 1,000,000 documents. `InMemoryVectorSearchOptions` can store
the same rows as signed bytes plus one scale per row instead, and scan them in the quantised domain;
this lane measures what that costs in ranking and what it saves in memory and time. It is a direct
run rather than a BenchmarkDotNet class for the scale lane's reason: the block it measures is the
thing being built, and a job that rebuilt it per invocation would measure the build. One engine per
representation is built per size, timed, and released before the next is built.

```bash
# the three smaller sizes in the table below
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- quantize --sizes 10000,100000,500000 --queries 200
# the 1,000,000 row, which is opt-in for the memory it holds
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- quantize --sizes 1000000 --queries 200
# the default lane: the three smaller sizes, 200 queries
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- quantize
```

The lane reads the cached `multilingual-e5-small` vectors the evaluation harness writes
(`bench/LexiSharp.Eval/data/<corpus>/e5-small.docs.bin`, `.queries.bin` and `.json`, produced by any
`--dense` run). With no cache it falls back to a deterministic Gaussian pool and says so in its
header; the rows below are from the cache.

**Representation.** Symmetric per-vector int8: `scale = max|v| / 127` and `qᵢ = round(vᵢ / scale)`
clamped to `[-127, 127]`, so the largest component of each row uses the full range and the stored row
is `scale · q`. The float32 block is not retained beside it. Two decisions worth naming:

- **The norm is recomputed in the quantised domain**, `Σqᵢ²`, accumulated once at insert over the
  integer components. The float vector's norm is *not* carried: it is the length of a vector the
  scan never reads, and dividing by it would make the quantised cosine of a document with itself
  less than 1 while leaving the vectors unchanged.
  `TheQuantizedCosineAgainstItsOwnStoredVectorIsOne` pins it. The stored quantity is the integer sum
  rather than `scale²·Σqᵢ²` because the per-vector scale is a common factor of the dot product and
  of the norm — it cancels — and a row whose components sit near the bottom of the float range would
  otherwise underflow its own squared norm to zero and silently stop matching.
- **The scale is still stored**, because it is what defines the dequantised row `Export` hands back
  and what keeps a re-imported corpus on the same components.

**Corpus and query set.** The real pool is the three caches the harness has on this host: ArguAna
(8,674 documents, 1,406 queries), NFCorpus (3,633, 3,237) and SciFact (5,183, 1,109) — 17,490
document vectors and 5,752 query vectors, all 384-dimensional unit vectors.

- **10,000 documents, real.** The first 10,000 pooled document vectors (ArguAna's 8,674, then
  NFCorpus's first 1,326).
- **100,000, 500,000 and 1,000,000 documents, synthetic.** No cached corpus here is larger than
  ArguAna's 8,674 documents, so a larger real corpus is not available without encoding one. Those
  rows are drawn from a **64-cluster Gaussian fitted to the real pool**: k-means centers, each
  document a draw around its center with that cluster's RMS residual, renormalized. The fit preserves
  the concentration of the real embedding distribution rather than assuming isotropic random
  directions, which is what makes the quantisation error comparable; it is still a fitted corpus, not
  a retrieval result, and the lane labels the column.
- **Queries: 200 real query vectors at every size**, the first 200 in the pool (ArguAna's), at
  `Limit: 10`. Recall@10 is the overlap between the int8 top 10 and the float32 top 10 for the same
  query on the same vectors.
- Both engines receive the vectors through `Import`, the stored-vector path, so no encoder runs and
  no ONNX session is in the measurement. Each is warmed for five seconds of wall time on the same
  calls that are timed, then 200 searches are timed individually.

**Measured on 2026-10-26**, on Manjaro Linux, Intel Core i7-8850H (12 logical processors),
.NET 10.0.13, `ServerGC=True`, 15 GiB of RAM. The six rows at 10,000–500,000 documents are two runs
of the first command (A, then B), taken while other sessions were building on the host, load average
3.2–3.3. The two 1,000,000-document rows are two runs of the second command after those sessions'
encoders had finished: load average 1.9 and 2.5 at the start of the two runs, 2.5 and 3.1 at the
end. Recall and the byte counts are deterministic and identical across all four runs; the timing
columns are each run's own.

A third 1,000,000-document run, started while two SPLADE encoders with 6 threads each were loading
the machine (load average 16–22 on the 12 logical processors), read p50 **286.5 / 171.2 ms** — 2.8×
and 2.9× the quiet values — with the same recall and the same byte counts. The latency columns are
read at the load stated for them; the recall and memory columns are not.

| Documents | Corpus | Representation | Engine B/vector | Payload B/vector | recall@10 | p50 (A / B) | mean (A / B) | ns/document (A / B) |
|---:|---|---|---:|---:|---:|---:|---:|---:|
| 10,000 | real | float32 | 2,635 | 1,540 | — | 1.00 / 0.84 ms | 1.02 / 0.87 ms | 101.7 / 86.6 |
| 10,000 | real | int8 | 754 | 392 | 0.9885 | 0.49 / 0.51 ms | 0.50 / 0.54 ms | 49.8 / 53.5 |
| 100,000 | synthetic | float32 | 2,117 | 1,540 | — | 9.12 / 9.24 ms | 9.18 / 9.79 ms | 91.8 / 97.9 |
| 100,000 | synthetic | int8 | 612 | 392 | 0.9735 | 5.57 / 5.72 ms | 5.61 / 5.79 ms | 56.1 / 57.9 |
| 500,000 | synthetic | float32 | 1,699 | 1,540 | — | 46.13 / 45.55 ms | 48.58 / 45.89 ms | 97.2 / 91.8 |
| 500,000 | synthetic | int8 | 495 | 392 | 0.9710 | 28.91 / 27.86 ms | 29.24 / 29.63 ms | 58.5 / 59.3 |
| 1,000,000 | synthetic | float32 | 1,701 | 1,540 | — | 103.93 / 102.97 ms | 104.52 / 103.38 ms | 104.5 / 103.4 |
| 1,000,000 | synthetic | int8 | 498 | 392 | 0.9755 | 59.75 / 59.79 ms | 59.99 / 60.18 ms | 60.0 / 60.2 |

**What the columns mean.**

- **Payload B/vector** is the layout's cost per stored row: `4·dimension + 4` for float32 (components
  plus the cached squared norm) and `dimension + 4 + 4` for int8 (components, scale, squared norm).
  At 384 dimensions that is **1,540 B against 392 B, a factor of 3.93** — a quarter of the payload,
  less the two scalars that do not shrink.
- **Engine B/vector** is the measured managed-heap delta of building the engine, divided by the
  document count: `GC.GetTotalMemory(forceFullCollection: true)` before and after the `Import`, with
  the corpus and the `SearchDocument` array allocated before the first reading so they are outside
  the delta. It is larger than the payload at 10,000 and 100,000 documents because the slot block
  doubles from 16 and the measured figure includes that slack (131,072 slots for 100,000 documents is
  31 % slack; 524,288 for 500,000 and 1,048,576 for 1,000,000 are 4.9 %) plus the per-slot document
  array and the two dictionaries keyed by document id, which cost the same in both representations.
  The measured ratio is therefore **3.4–3.5×** rather than 3.93×, and a corpus that lands just under
  a power of two gets the larger ending.
- **recall@10** is the fraction of the float page's slots the int8 page reproduces. On the real
  10,000-document row it is **0.9885**; on the fitted corpora it is **0.9735**, **0.9710** and
  **0.9755** at 100,000, 500,000 and 1,000,000. More documents means more near-ties for quantisation
  to reorder, but the three fitted points are not monotone in size and none of them is a trend line
  for a real corpus: the only real point is at 10,000.
- **ns/document** divides the total measured milliseconds by documents × queries. It is the scan's
  per-candidate cost, and it is what the latency difference actually is: 87–105 ns for float32
  against 50–60 ns for int8, a little over half at every size (1.6× at 500,000, 1.7× at 1,000,000).
  The int8 row moves a quarter of the bytes in about half the time, so the scan is **not
  memory-bandwidth-bound at these sizes**: the widening (`sbyte → short → int`), the conversion to
  float and the multiply cost more per element than the float32 path's two-chained FMA, and a kernel
  built for integer dot products would have more headroom. That is not measured here, and the
  **1.6–2.0×** p50 ratio is what this kernel reads.

**What this changes about the default.** Nothing. `InMemoryVectorSearchOptions.Quantization`
defaults to `VectorQuantization.None`, the float32 block and its scan are the code path they were,
and `VectorQuantizationTests` pins the default engine's `Export` to the provider's vectors exactly.
Quantisation is opt-in because a 1–3 % loss of the float page is a property of the model and the
corpus: 0.9885 on `multilingual-e5-small` is a measurement of this model on this corpus, not a
tolerance for another one.

**What the 1,000,000 row costs to take.** It is opt-in because of the memory, and that memory is
measured: across the two runs the host's used total peaked at **9.5 GiB of its 15 GiB**, and
**5.7 GiB** stayed available. The engine's managed heap is 1,701 B per vector for float32 and 498 B
for int8, so at 1,000,000 documents the block is **1.70 GB against 498 MB**, a ratio of 3.4× rather
than the payload's 3.93×. The lane holds the corpus, one engine and the `SearchDocument` array at
once and releases each size before building the next.

**Not measured.** Other embedding models, other dimensions (only 384 was measured; a smaller dimension
pays proportionally more for the scale and norm, a larger one less), dot-product rather than cosine
(the engine is cosine-only), ANN indexes, structured filters, `MinimumScore`, concurrency, and any
query set but the 200 ArguAna ones named above. The latency columns are `p50` and `mean` over 200
searches; no percentile beyond that, no distribution, and the six smaller rows were taken on a shared
host at the load stated above. The memory figure is managed heap only — no native, JIT, thread or
GC-structure memory — and the per-vector dictionaries are included at their measured cost, not
divided out.

## Concurrency lane (`concurrency`): one index at 1, 2, 4 and 8 callers

Every other timing on this page is single-threaded. `ConcurrentSearchTests` establishes that
concurrent read-only searches return what a lone search returns; it says nothing about throughput or
tail latency. The concurrency lane is the measurement for the question a server asks: one read-only
index, several callers, and what the opt-in `SynchronizedTextSearchEngine`'s read lock costs when the
wrapped and unwrapped engines are measured against each other in the same session.

```bash
# five interleaved runs per row — the measurement below
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- concurrency --runs 5
# the default is three runs per row
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- concurrency
```

A plain `dotnet run --project bench/LexiSharp.Benchmarks -c Release` still runs the BenchmarkDotNet
suite and nothing else.

**Corpus, queries and method.**

- One `InMemoryTextIndex` built from `CorpusFactory.CreateZipfDocuments(100_000, 50, 30_000)` — Zipf
  exponent 1.07, 50 words per document, a 30,000-term vocabulary — and one `RankedTextSearchEngine`
  over it with the default tokenizer and `Bm25Scorer`. Every worker searches that same instance.
- Three queries are issued round-robin 1:1:1, so the mix is not only the cheap case:

  | shape | text | `df` | hits at `Limit: 10` |
  |---|---:|---:|---:|
  | `tail-1` | `w20000x` | 9 | 9 |
  | `tail-3` | `w20000x w25000x w29000x` | 29 (sum) | 10 |
  | `head-1` | `w1x` | 95,402 | 10 |

  The head query is in 95.4 % of the corpus and costs about a millisecond; the two tail queries cost
  microseconds.
- Each shape — one engine kind at one caller count — is warmed for **five seconds of wall time**, not
  a call count, because tiered compilation promotes the search chain at roughly 0.5–0.7 s on this
  host; the scale lane's section above records the measurement that established it. Workers are
  dedicated threads released from a gate together, not pool threads.
- Each run is a **2 s timing pass plus a separate 1 s allocation pass**. Throughput is completed
  searches divided by the wall time from gate release to the last worker's exit; p50 and p99 are
  nearest-rank over every call the run made; allocation per search sums each worker's
  `GC.GetAllocatedBytesForCurrentThread` delta and divides by the calls, after 64 unpriced calls per
  worker so the pooled score buffers are in place.
- Five runs per row. Raw and synchronized alternate within a run number and the order flips on every
  run, so neither side is always measured second. The per-call interval contains two
  `Stopwatch.GetTimestamp()` reads and no correction is applied.

**Host, and how quiet it was.** Measured on 2026-10-25 starting 22:16 local time: Manjaro Linux,
Intel Core i7-8850H (6 physical cores, 12 logical processors), .NET 10.0.13, `ServerGC=True`,
latency mode `Interactive`, 15.2 GiB of RAM. `vmstat` read the host 90 % idle immediately before the
run; the 1-minute load average was **1.9 at the start and 5.7 at the end**, the latter including this
lane's own eight worker threads. `ps` showed no other agent's build or test process running at the
start of the pass. A second
five-run pass eight minutes earlier read 2,867 / 5,562 / 9,490 / 12,329 searches/s for the four raw
rows — **within 2 %** of the table below in both directions. An earlier pass made while another
session's test suite was running read 1,952 searches/s at one caller raw against 2,841 here, 31 %
low; it is not used. The absolute rates are this host's and this hour's; the raw/synchronized
comparison is what the interleaving buys, and the spread is stated with every row.

**Measured — median of five runs, min–max across those runs in parentheses:**

| callers | engine | throughput (searches/s) | mix p50 | mix p99 | allocation/search |
|---:|---|---:|---:|---:|---:|
| 1 | raw | 2,841 (2,765–2,872) | 4.73 µs (4.61–4.94) | 1.34 ms (1.29–1.80) | 669 B |
| 1 | synchronized | 2,858 (2,818–2,875) | 4.69 µs (4.46–4.82) | 1.35 ms (1.29–1.42) | 669 B |
| 2 | raw | 5,630 (5,606–5,667) | 4.58 µs (4.49–4.67) | 1.39 ms (1.34–1.57) | 669 B |
| 2 | synchronized | 5,634 (5,553–5,666) | 4.74 µs (4.56–4.84) | 1.39 ms (1.36–1.53) | 669 B |
| 4 | raw | 9,636 (9,290–10,312) | 5.70 µs (5.28–6.02) | 1.66 ms (1.57–1.80) | 669 B |
| 4 | synchronized | 9,603 (9,309–9,630) | 5.78 µs (5.68–6.13) | 1.69 ms (1.59–1.93) | 669 B |
| 8 | raw | 12,068 (11,768–12,346) | 9.54 µs (9.36–9.66) | 2.75 ms (2.74–2.77) | 669 B |
| 8 | synchronized | 12,279 (12,007–12,419) | 9.58 µs (9.49–9.62) | 2.77 ms (2.74–2.78) | 669 B |

The allocation column is the five-run median; the per-run range is 669–673 B on every row.

`synchronized / raw`, as the ratio of the five-run medians with the per-run ratio range beside it:

| callers | throughput | mix p50 | mix p99 |
|---:|---:|---:|---:|
| 1 | 1.006× (0.992–1.028) | 0.990× (0.933–1.017) | 1.006× (0.764–1.058) |
| 2 | 1.001× (0.990–1.006) | 1.035× (1.013–1.061) | 1.001× (0.934–1.024) |
| 4 | 0.997× (0.934–1.037) | 1.014× (0.944–1.092) | 1.019× (0.939–1.126) |
| 8 | 1.017× (0.973–1.046) | 1.004× (0.982–1.019) | 1.007× (0.997–1.009) |

Per query, median of the five run `p50` / `p99` values:

| callers | engine | `tail-1` p50 / p99 | `tail-3` p50 / p99 | `head-1` p50 / p99 |
|---:|---|---:|---:|---:|
| 1 | raw | 3.74 / 21.74 µs | 4.43 / 19.04 µs | 1.02 / 1.48 ms |
| 1 | synchronized | 3.71 / 20.97 µs | 4.34 / 21.27 µs | 1.01 / 1.53 ms |
| 2 | raw | 3.77 / 13.28 µs | 4.44 / 15.33 µs | 1.02 / 1.77 ms |
| 2 | synchronized | 3.87 / 14.92 µs | 4.53 / 14.16 µs | 1.01 / 1.66 ms |
| 4 | raw | 4.50 / 21.35 µs | 5.34 / 22.78 µs | 1.18 / 1.92 ms |
| 4 | synchronized | 4.57 / 23.48 µs | 5.41 / 25.54 µs | 1.19 / 1.95 ms |
| 8 | raw | 7.65 / 17.16 µs | 8.67 / 17.00 µs | 1.56 / 2.77 ms |
| 8 | synchronized | 7.63 / 16.71 µs | 8.28 / 17.61 µs | 1.51 / 2.80 ms |

**What the rows say.**

- **The read lock is at the edge of this lane's resolution, and no direction is established.**
  Throughput is at parity at every caller count — the medians are 0.997×–1.017×, and every per-run
  range includes 1.0. The mix p99 medians are 1.001×–1.019×, and those ranges include 1.0 too, though
  one four-caller p99 run reached 1.126×, so the median statement is not a bound on the tail. The
  one per-run range that excludes 1.0 is the mix **p50 at two callers**: 1.013×–1.061×, a 3.5 %
  median increase. The same comparison is 1 % *lower* at one caller and 0.4 % higher at eight, so it
  does not describe a fixed cost that survives the caller count, and it is left as this host's
  drift rather than promoted to a finding. The honest bound is that, on the median, the read pair
  costs at most a few percent of a 4–10 µs tail call here, which confirms the conclusion the type's
  remarks state while making the measurement reproducible.
- **Throughput scales sublinearly, and the head query is why.** Relative to one caller, the raw rows
  read 1.98× at two callers, 3.39× at four and 4.25× at eight. The `head-1` p50 rises from 1.02 ms at
  one caller to 1.18 ms at four and 1.56 ms at eight, and the tail p50s roughly double, because
  eight workers on six physical cores are sharing the same execution units and memory bandwidth; the
  lane measures callers, not cores.
- **The mix p99 is the head query's tail, not the tail queries'.** Because the mix is 1:1:1, the 99th
  percentile of all calls falls inside the head distribution; the per-query table is what separates
  them. Per-query p99 across caller counts is noisy in both directions (13.3–23.5 µs for `tail-1`
  across the eight rows) and is not a scaling curve; read the mix row for load and the per-query
  p50 rows for shape.
- **Allocation is 669 B/search in every row.** Both engines and all four caller counts read the same
  five-run median, with a 669–673 B per-run range: the wrapper adds no measurable allocation, and the
  engine's rented score buffers return to `ArrayPool` rather than being charged per call. The 669 B is
  the returned page and the query's fixed cost, not index memory; this lane does not re-measure the
  index's retained bytes, which the scale lane reports separately.
- **The four-caller row's spread is larger than any difference in it.** Its raw throughput spans
  9,290–10,312 searches/s around a 9,636 median, while the synchronized median is 9,603; the per-run
  throughput ratio ranges 0.934–1.037. No direction is claimed there. The same caveat applies to the
  one-caller p99 ratio (0.764–1.058), whose low end is one raw run at 1.80 ms against a 1.34 ms
  median.

**What this lane does not measure.** Other machines, other core counts and NUMA placement; the
effect of SMT or CPU affinity; write concurrency — `Add`, `Remove`, `Clear` or `Index` while readers
search, which is the case `SynchronizedTextSearchEngine`'s write-lock remarks describe and this lane
does not touch; `AtomicEngineReference`'s swap path; filtered queries; other analyzers and scorers;
corpora other than 100,000 documents; sustained load beyond a two-second window; GC pause
attribution; and any latency statistic beyond p50 and p99 (no p99.9, max or full distribution).
`SearchOptions.Trace` and `SearchOptions.Costs` are also outside it: both are mutable per-request
collectors that a concurrent caller must give one instance per search (see
[observability](observability.md#per-query-cost-sheet-searchcosts)), and the lane attaches neither.
Correctness under concurrency is `ConcurrentSearchTests`'s job, not this lane's.


## Tokenizer (`TokenizerBenchmarks`)

Tokenizing the same text under different normalizations. The `Ratio` column is only
meaningful within the `Tokenize*` rows — the `Normalize*` methods operate on a much smaller
input:

| Method               | Mean          | Error        | StdDev        | Ratio | Allocated |
|--------------------- |--------------:|-------------:|--------------:|------:|----------:|
| TokenizeDefaultAscii |  97,527.99 ns | 1,822.854 ns |  2,995.002 ns | 1.000 | 138,616 B |
| TokenizeAccented     | 470,094.28 ns | 9,327.037 ns | 15,324.590 ns | 4.824 | 329,824 B |
| TokenizeMixedUnicode | 214,949.24 ns | 4,275.402 ns |  7,709.432 ns | 2.206 |  97,096 B |
| NormalizeAsciiOnly   |      32.69 ns |     0.678 ns |      0.696 ns | 0.000 |         - |
| NormalizeAccented    |     943.57 ns |    18.918 ns |     46.406 ns | 0.010 |     648 B |

Read at this scale (roughly): ASCII-only input tokenizes ~5× faster than input requiring
accent removal, and allocations scale accordingly. Absolute numbers will differ on any other
machine.

## Similarity (`LevenshteinBenchmarks`)

| Method                  | Mean     | Error     | StdDev    | Allocated |
|------------------------ |---------:|----------:|----------:|----------:|
| DistanceAgainstVocabulary | 1.320 us | 0.0204 us | 0.0191 us |         - |
| DistanceSimilarPairs      | 1.456 us | 0.0289 us | 0.0682 us |         - |

## Classification (`ClassificationBenchmarks`)

Synthetic in-memory Naive Bayes classifier (no external data or network):

| Method          | Mean        | Error     | StdDev    | Allocated  |
|---------------- |------------:|----------:|----------:|-----------:|
| TrainClassifier | 2,097.52 us | 30.799 us | 25.719 us | 1571.28 KB |
| PredictText     |    10.59 us |  0.209 us |  0.280 us |    9.44 KB |

## Behavioural gate (`lexisharp verify`)

Not a benchmark, and the distinction matters. `verify` replays the reference corpus against a
committed baseline and fails when any ranking or metric moves — a regression test made of retrieval
outputs:

```bash
dotnet run --project bench/LexiSharp.Cli -c Release -- verify bench/reference-corpus/corpus \
    --queries bench/reference-corpus/queries.json --qrels bench/reference-corpus/qrels.tsv \
    --configs bm25,bm25-semantic,bm25f,bm25+,bm25l,bm25-proximity-full --top-k 5 \
    --against bench/reference-corpus/golden/rankings.txt
```

It runs in the `core` CI job and gates the build, which no benchmark here does and none ever should.
It covers six configurations, four of which are there specifically to put a scorer's *default*
parameters through a real ranking — see the reference corpus README for the config list and for what
this still cannot catch.
It is worth running before trusting any number in this file: a baseline only means something if the
code still reproduces it. See the
[reference corpus README](https://github.com/manuc66/LexiSharp/blob/main/bench/reference-corpus/README.md#the-golden-master).

## Reproduce

```bash
dotnet run --project bench/LexiSharp.Benchmarks -c Release
# search-only subset (fast):
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- --filter '*Search*'
# the term-at-a-time crossover, both loops on the same query:
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- --filter '*ScoringPath*'
# where a query's bytes go, component by component:
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- --filter '*QueryAllocationBreakdown*'
# index build and tail-query cost per corpus size (see the scale lane above):
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- scale
# tail-query p50/p99, allocation and the cursor pass's density crossing (see the cursor lane above):
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- cursor 100000 --sweep --both
# posting entries a block bound can skip, per query and corpus size (see the pruning lane above):
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- wand 100000
# one shared index at 1/2/4/8 callers, raw and synchronized (see the concurrency lane above):
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- concurrency --runs 5
```

The three-corpus-size crossover above is **not** a report from this project: a 1,000,000-document
index needs 7.1 GB resident and 57 s to build in that harness, which is not something a CI job
should do on every push. The [scale lane](#scale-lane-scale-index-build-and-tail-queries-per-corpus-size)
is the in-project command that reaches the size; it reports build time, retained bytes and
tail-query latency, not the fixed-cost / per-posting-entry fit the table above is made of, so the
two are not interchangeable. Its 1,000,000-document size is opt-in and was not run in the session
that measured the 10,000- and 100,000-document rows documented there.

Reports land in `BenchmarkDotNet.Artifacts/results/` (CSV, HTML, GitHub-flavored markdown).
For stable numbers use the default (non-`ShortRun`) job configuration and publish the full
report, not a summary.