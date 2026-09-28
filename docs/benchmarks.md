---
title: Benchmarks
nav_order: 12
---

# Benchmarks

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

The `Search allocation (advisory)` CI job re-measures this on a second host on every run, and
uploads the raw report with the machine configuration next to it. **Read only its `Allocated`
column.** Allocation is counted in bytes and does not depend on how fast the host is, so that number
travels; the `Mean` column does not. These are Azure VMs shared between tenants, where host-level
contention is a documented source of variance, and the effect being measured here is a few
percent — a shared runner cannot resolve it, and a `Mean` copied from that artifact into this file
would not be a measurement. The job is `continue-on-error` and gates nothing, deliberately.

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

### Measured but not shipped: term-at-a-time scoring

Every range scorer is document-at-a-time: `Bm25QueryPlan.Score` asks `index.TermFrequency(documentId,
term)` for each (document, term) pair, and that is two string-dictionary lookups. A prototype that
walks CSR posting lists into a score accumulator indexed by an integer document ordinal — one
dictionary lookup per *term*, then integer arithmetic per posting — measured, as originally
recorded, **0.097 ms against 2.00 ms** for a 2-term query over 10,000 documents. Both figures
below are re-measurements; treat the original pair as a prototype note, not a shipped number.

**Re-measured, that 20× is real but it is measured in the one regime where the engine deliberately
does not use candidate generation.** The 20× baseline is a 2-term query on the 38-word benchmark
corpus, where each term is in ~74% of documents, the summed document frequencies exceed half the
corpus, and the engine's own `sum(df)/Count < 0.5` test sends it to a full scan instead. Re-run
against that corpus (min of 200 iterations, load average 2.2), a TAT-over-CSR prototype measures
0.020–0.067 ms with a fresh accumulator, or 0.041–0.212 ms with a reused one, against 0.83–4.11 ms
for the engine — 12× to 42× depending on the query. The parity is the good news: contributions
added per document in query-term order sum identically, so every top 10 came back identical to the
engine down to the last bit, and the golden master is not at risk.

On a realistic term distribution, though, the engine already enumerates candidates and is already
fast, so there is very little left for loop inversion to win:

| Zipf corpus, 10k docs | df sum | engine today | TAT over CSR | DAAT over CSR |
|-----------------------|-------:|-------------:|-------------:|--------------:|
| `w500x`               |     97 |      0.013 ms |     0.003 ms |      0.002 ms |
| `w2000x w9000x`       |     35 |      0.007 ms |     0.001 ms |      0.001 ms |
| `w20000x w25000x w29000x` |  2 |      0.001 ms |   < 0.001 ms |    < 0.001 ms |
| `w1x w500x w9000x`    |  9,628 |      1.394 ms |     0.024 ms |      0.001 ms |

The last row is the only one where the choice of inversion matters, because it is the only one where
one term is rare and the others are not. Absolute headroom on shaped queries is microseconds, not
milliseconds. The 20× is a ratio against a baseline that mostly does not occur, which is the
« tuned vs tuned » mistake in a different costume: one side measured in a regime the other side
would never be in.

Two further costs the prototype does not carry. Its accumulator is 8 bytes × document count **per
concurrent query** — 80 KB at 10,000 documents, 8 MB at a million — and the allocation-free version
of it, a generation-stamped scratch reused across queries, measured 2× to 4× slower than the fresh
one (0.117 ms against 0.067 ms, and 0.212 ms against 0.054 ms on the widest query), so the scratch
has to be pooled and the pooling is part of the design.
And with CSR the position lists stop being dictionary lookups: `GetTermPositions` becomes a binary
search called per (document, phrase term) by the phrase gate and by `ProximityReranker`, and
`GetTerms` needs a per-document position→term structure, i.e. a second inverted layout.

**If the loop is ever inverted, invert it document-at-a-time over sorted CSR lists, not
term-at-a-time.** A two-pointer merge driven by the *smallest* document frequency, abandoning a
term as soon as its cursor runs off the end, is the `DAAT` column above: 0.001 ms against 0.024 ms
for TAT on the mixed-frequency query. It needs no corpus-sized accumulator, no document ordinals and
no rebuild-on-mutation strategy, and it allocates nothing. Its cost is driven by the rarest term
rather than the sum of the frequencies, which is the property that matters as corpora grow.

Storage is the other half. The claim was 26.1 MB of nested posting dictionaries becoming ~2.1 MB of
`int[]`; re-measured on the same corpus (279,832 (term, document) pairs, 500,000 positions) the
direction holds and the ratio is larger — 36.78 MB nested against 2.90 MB of `int[]` for document
ids and term frequencies, plus 1.91 MB for the position block the phrase gate still needs, so
4.81 MB total, about 7.6×. The two absolute figures in the original claim are not reproducible as
written and the 12× ratio should not be quoted; the order of magnitude is the part that survives.

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
```

Reports land in `BenchmarkDotNet.Artifacts/results/` (CSV, HTML, GitHub-flavored markdown).
For stable numbers use the default (non-`ShortRun`) job configuration and publish the full
report, not a summary.