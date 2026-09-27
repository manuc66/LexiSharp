---
title: Benchmarks
permalink: pretty
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