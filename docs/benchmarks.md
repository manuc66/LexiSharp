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

| Method                          | Mean      | Error     | StdDev    | Allocated |
|-------------------------------- |----------:|----------:|----------:|----------:|
| Bm25Search                      |  2.336 ms | 0.1464 ms | 0.4316 ms |   1.33 KB |
| TfIdfSearch                     |  2.197 ms | 0.1709 ms | 0.5040 ms |    1.3 KB |
| QueryLikelihoodSearch           |  3.009 ms | 0.1619 ms | 0.4566 ms |   1.35 KB |
| BooleanSearch                   |  1.077 ms | 0.0328 ms | 0.0945 ms |   1.19 KB |
| Bm25SearchRunAllQueries         | 24.283 ms | 0.5560 ms | 1.6395 ms |   6.87 KB |

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
that artifact into this file would not be a measurement. The workflow gates nothing, deliberately.

It also runs on no schedule and on no push, because nothing reads its output on a schedule either:
there is no committed baseline to compare against and no threshold to trip, so the reader is a person
opening a run. Across a week on every push it reported the same `Allocated` column byte for byte on
five consecutive runs at unchanged code, at a cost of roughly nine runner-hours. Dispatch it after a
commit that touches the index or the scoring path. A future version that compares against a
committed baseline could gate, and would then be worth running continuously.

What *does* gate, deterministically, is `lexisharp verify` in the `core` CI job: it replays this
corpus against the committed baseline and fails the build on any drift in ranking or metrics. No
clock is involved, so there is nothing to be noisy — which is the difference between a benchmark
and a regression test, and the reason behaviour is protected that way rather than by a threshold.

### Before / after the search optimization pass (same env)

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

**Dense search, 4.5×.** `VectorSimilarity` now runs on `Vector<float>` (AVX2/AVX-512 where the host
has it) with two independent accumulator chains, and the engine computes each document's squared
norm once at insert time, so a scan costs a dot product per document instead of a dot product
plus two norm accumulations. Vectors moved into one contiguous dimension-strided block, so a scan
walks memory linearly rather than a dictionary entry per document. The page is cut by a bounded
worst-first window instead of materializing and sorting every document that scored above zero,
which is where the 99.3 % allocation drop comes from: the old path built a `SearchResult` for all
10,000 documents and a LINQ sort chain on top, to return 10 rows. The worst "after" sample
(2,206 µs) still beats the best "baseline" sample (4,900 µs), so the distributions do not overlap.

**Index size, −31 %.** The tokenizer allocates a fresh string per token occurrence, so the index
was holding one string object per *token* — 500,000 objects to represent 38 distinct words on
this corpus. `InMemoryTextIndex` now keeps one instance per term in its posting list and rewrites
each occurrence to it, so the corpus holds a vocabulary. It costs no extra hashing, because
resolving a term to its posting list was already a required lookup; the shared instance rides
along. Build allocation rises 5.3 % (a second, exactly-sized token list per document) and build
time is unchanged within this host's resolution.

**Lexical search: unchanged, and that is a measurement rather than an absence of one.** The pass
touched the scoring path only through the shared top-L window. It allocates 5.9 % less per search
and, timed, is indistinguishable from the baseline in a three-way interleaved run (minima
2,920 / 2,945 / 2,877 µs for baseline / with-window / with-window-reverted). The third build
exists to make that check falsifiable.

**Naive Bayes, −24 %.** The per-token idf weight and vocabulary flag were recomputed once per
class inside the scoring loop, i.e. a dictionary lookup per (class, token) pair. They are now
resolved once per prediction into a single array, and the corpus count is materialized only when
`Complement` is on, so a default model pays for one array rather than five. Costs 5 % more
allocation.

**Correction, later:** the same pass had also made the *vocabulary membership lookup itself*
conditional on `SkipOutOfVocabularyTokens`, which is wrong — `IdfWeight` reads the
document-frequency table and needs the answer whatever the options say, so an
out-of-vocabulary query term threw `KeyNotFoundException` on any model with a non-default
`IdfMode`. Membership is now resolved unconditionally and the flag array is still only
materialized when `SkipOutOfVocabularyTokens` is on. The cost is one `TryGetValue` per query
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
corpus order, and honouring that cost a walk of the *entire* corpus after the union of the posting
lists — O(corpus) however few documents matched. A 3-term query whose candidate set was 2 documents
out of 10,000 still paid for 10,000 documents. The engine now asks for the set through an internal
`IUnorderedCandidateIndex` capability and the walk is gone. The public method keeps its documented
order, so this is the engine declining to pay for it, not the contract being narrowed.

This is safe because the ranking order was already total and never depended on candidate order:
`TopRankedWindow` orders by score descending with ties broken by ordinal document id. That was
already documented and it is now checked. `CandidateEnumerationOrderTests` builds a corpus of 100
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

This section replaces an earlier one headed *Measured but not shipped: term-at-a-time scoring*, which
recorded the same analysis and reached a different conclusion. The work is shipped; the history of
how the original 20× turned out to be measured in the wrong regime is kept below, because it is the
reason the benchmark corpus had to change.

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
document's **term frequency and length** now folds the inverted lists straight into an
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
  `ScoreAccumulatorTests` now pins it: remove the clear and that test fails, `Count` 0 against 512.

**Search, head terms** (`CandidateSearchBenchmarks`, Zipf 10,000 × 50 words, s = 1.07). This is
the representative table: a real term distribution, and the three head rows are new. No class here
previously reached the regime where the engine scans the whole corpus, because the 38-word corpus
puts every term in ~74% of documents — analytically (37/38)^50 ≈ 0.264, so 74% of 10,000 documents
contain any given word. That figure is inherited from the existing benchmark comments, not
re-measured here, and it is the number the next table below is *not* about.

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

**The speedup tracks the cost the old loop already had, not the query's rarity as such.** Per unit
of work: the document-at-a-time loop costs ~96 ns per document it scores (one `DocumentLength`
hash plus one `TermFrequency` per query term, each a string hash), and the term-at-a-time pass
costs ~13 ns per posting entry. That ~7× slope is the mechanism; the ratio reaches 10-15× at the
head because the new path's fixed cost — clearing a corpus-sized buffer — has stopped mattering by
then, and is *below* 1× at df = 1 for the same reason. So a query that already took 200 ns does not
gain, and a query that took 1.6 ms does, which is a different and more useful statement than
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
  threshold. Scaling the threshold on the fixed cost alone — which is what the first version of this
  did, with a divisor of 1,024 — gives 9 / 97 / 976 and hands back a **1.2-2.4× win at two of the
  three scales**. The divisor is now 20,000, fitted to the three measured crossings, which gives
  8 / 8 / 50.

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

That 1.03× is worth a note, because the first version of this measurement said 1.31× *slower* and
was wrong. The per-document row on the new build was allocating 321 KB per search (see the
`PassesFilters` section below); 288 KB of that was a per-candidate enumerator, and the resulting
garbage-collection pressure was being charged to the loop as if it were work. The number moved when
the allocation was removed, not when the code did. **A time number measured on a path that allocates
a quarter of a megabyte per call is a measurement of the allocator**, and the honest reading of
"the restructuring costs 31%" was "the restructuring exposed an allocation bug".

**A pre-existing allocation bug this pass turned up, which the 321 KB above led to.**
`SearchOptions.PassesFilters` is called once per candidate document — on a filtered full scan,
once per document in the corpus. It walked the filter list with a `foreach` over an
`IReadOnlyList<MetadataFilter>`, so the enumerator was resolved *through the interface* and
heap-allocated on every call. The comment sitting next to that loop explained that a LINQ `All()`
had been avoided because it would allocate an enumerator per candidate; the `foreach` it annotated
allocated one per candidate anyway. Measured on a 10,000-document corpus with one filter that
rejects nothing: **288,072 bytes per search before, 72 after** — and the same search without a
filter allocates 1,224. This is not caused by the scoring work and is present in the pre-change
build; it is reported here because it is what the 2×2 measurement ran into.
`FilteredSearchAllocationTests` pins it with an allocation assertion (reverting the loop makes it
fail at 317,587 bytes per query) rather than a stopwatch, because that is the property that
regressed.

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

#### How the original 20× turned out to be measured in the wrong regime

Worth keeping, because the failure is not obvious. The first prototype — CSR posting lists into a
score accumulator indexed by an integer document ordinal — measured, as originally recorded,
**0.097 ms against 2.00 ms** for a 2-term query over 10,000 documents. Re-measured, that 20× is real
*and* it is measured in the one regime where the engine deliberately does not use candidate
generation: the 38-word benchmark corpus puts every term in ~74% of documents, so `sum(df)/Count <
0.5` fails and the engine full-scans. On a Zipf corpus, where the candidate path is the one real
queries take, the engine was already at 0.001–0.013 ms and a term-at-a-time prototype had
microseconds left to win. Parity was *not* the obstacle — the prototype's scores came back
bit-identical to the engine's, which is why this shipped rather than being abandoned.

| Zipf corpus, 10k docs | df sum | engine then | TAT over CSR | DAAT over CSR |
|-----------------------|-------:|-------------:|-------------:|--------------:|
| `w500x`               |     97 |      0.013 ms |     0.003 ms |      0.002 ms |
| `w2000x w9000x`       |     35 |      0.007 ms |     0.001 ms |      0.001 ms |
| `w20000x w25000x w29000x` |    2 |      0.001 ms |   < 0.001 ms |    < 0.001 ms |
| `w1x w500x w9000x`    |  9,628 |      1.394 ms |     0.024 ms |      0.001 ms |

The 20× is a ratio against a baseline that mostly does not occur, which is the « tuned vs tuned »
mistake in a different costume: one side measured in a regime the other side would never be in.
The conclusion that survived — that the head terms are where the money is — is the one the shipped
tables above confirm, but it took a benchmark corpus that could *reach* the path to confirm it, and
that is exactly what the old table could not have told anyone. The engine also has to grow a
threshold now, which the original 20× did not suggest it would need; see the crossover table.

Two costs the prototype was measured carrying, and how they were resolved:

- Its accumulator is 8 bytes × document count **per concurrent query** — 80 KB at 10,000
  documents, 8 MB at a million. It is rented from `ArrayPool` per search and returned, so a steady
  stream of searches allocates nothing after the first few; the memory tables above are the
  measured consequence, not a projection.
- With CSR the position lists would stop being dictionary lookups: `GetTermPositions` would become a
  binary search called per (document, phrase term) by the phrase gate and by `ProximityReranker`.
  That did not happen here — the flat arrays carry ordinals and frequencies only, and the
  authoritative `Dictionary<string, List<int>>` keeps serving positions — so the phrase gate and
  `ProximityReranker` are unchanged, at the cost of the pointer-chasing walk remaining in the write
  path. Replacing the authoritative form is a larger change than this one and is not attempted.

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
```

The three-corpus-size crossover above is **not** in the benchmark project: a 1,000,000-document
index needs 7.1 GB resident and 57 s to build, which is not something a CI job should do on every
push. It is a stopwatch harness run deliberately, and the numbers are on this page with the
conditions they were taken under.

Reports land in `BenchmarkDotNet.Artifacts/results/` (CSV, HTML, GitHub-flavored markdown).
For stable numbers use the default (non-`ShortRun`) job configuration and publish the full
report, not a summary.