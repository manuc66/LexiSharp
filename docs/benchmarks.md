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
resolved once per prediction into a single array, and the corpus count and vocabulary flag are
materialized only when `Complement` / `SkipOutOfVocabularyTokens` are on, so a default model pays
for one array rather than five. Costs 5 % more allocation.

Behaviour is unchanged: the full test suite passes and `lexisharp verify` reports 132 unchanged,
0 changed against the golden master. The `VectorSimilarity` kernels change the summation order,
which the existing property tests cover with their tolerances (1e-6 symmetric, 1e-3
scale-invariant).

### Measured but not shipped: term-at-a-time scoring

The largest remaining win is structural, and it was prototyped rather than landed. Every range
scorer is document-at-a-time: `Bm25QueryPlan.Score` asks `index.TermFrequency(documentId, term)`
for each (document, term) pair, and that is two string-dictionary lookups. On this corpus
10,000 × 8 of those lookups cost ~8 ms, and a 2-term query spends most of its 2.0 ms in
`GetCandidateDocuments` alone.

A prototype that walks CSR posting lists into a score accumulator indexed by an integer document
ordinal — one dictionary lookup per *term*, then integer arithmetic per posting — measured
**0.097 ms against the current 2.00 ms** for the same 2-term query over 10,000 documents: a 20×
difference. The same layout would also replace the 26.1 MB of nested posting dictionaries with
about 2.1 MB of `int[]`, measured the same way.

It is not in this pass because it cannot be done as a local change. The win depends entirely on
inverting the loop, and the interface is `TermFrequency(documentId, term)`, so a scorer can only
reach postings one document at a time. It needs document ordinals in the index plus a
posting-list capability interface, and the storage has to stay mutable for
`Add`/`Remove`/`AddExpansionTerms` or be rebuilt on mutation. The 20× is a prototype
measurement on this corpus, not a shipped number.

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