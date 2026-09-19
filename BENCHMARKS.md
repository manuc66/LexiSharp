# Benchmarks

Numbers in this file come from BenchmarkDotNet runs on a single, aging developer machine —
they are **indicative only**, not a performance claim. Run the suite yourself before drawing
any conclusion about your own corpus and hardware.

## Environment (for both tables below)

```
BenchmarkDotNet v0.14.0, Manjaro Linux
Intel Core i7-4790 CPU 3.60GHz (Haswell), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.111
Runtime: .NET 8.0.30 (8.0.3026.36720), X64 RyuJIT AVX2
Job: ShortRun (IterationCount=3, WarmupCount=3, LaunchCount=1)
```

**Caveat:** `ShortRun` keeps only 3 iterations, so the error bars are wide and some runs are
noisy (visible in the Error column). Treat every number as a rough order of magnitude.

## Index building (`IndexBenchmarks`)

Building an `InMemoryTextIndex` from scratch, 50 words per document. Gen0/Gen1/Gen2 columns
are GC collection counts per 1,000 operations (BenchmarkDotNet convention):

| Method                            | DocumentCount | WordsPerDocument | Mean      | Error        | StdDev    | Gen0      | Gen1     | Gen2     | Allocated |
|---------------------------------- |-------------- |----------------- |----------:|-------------:|----------:|----------:|---------:|---------:|----------:|
| BuildInvertedIndex                | 1000          | 50               | 21.73 ms  | ±32.26 ms    | 1.768 ms  | 218.7500  | 93.7500  | -        | 25.49 MB  |
| BuildIndexWithStopWordsAndStemmer | 1000          | 50               | 21.92 ms  | ±13.70 ms    | 0.751 ms  | 218.7500  | 156.2500 | -        | 25.53 MB  |
| BuildInvertedIndex                | 10000         | 50               | 255.19 ms | ±35.99 ms    | 1.973 ms  | 1000.0000 | 500.0000 | 500.0000 | 253.27 MB |
| BuildIndexWithStopWordsAndStemmer | 10000         | 50               | 318.15 ms | ±1,006.40 ms | 55.164 ms | 1000.0000 | 500.0000 | 500.0000 | 253.67 MB |

Read at this scale (roughly): a 10k-document, 50-word corpus indexes in ~0.25–0.32 s and
allocates ~254 MB in the process. Nothing is claimed here about larger corpora, search
latency under load, or memory behavior beyond a single run.

## Tokenizer (`TokenizerBenchmarks`)

Tokenizing the same text under different normalizations. The `Ratio` column is only
meaningful within the `Tokenize*` rows — the `Normalize*` methods operate on a much smaller
input:

| Method               | Mean           | Error         | StdDev       | Ratio | Allocated |
|--------------------- |---------------:|--------------:|-------------:|------:|----------:|
| TokenizeDefaultAscii | 103,615.00 ns  | ±12,434.62 ns | 681.583 ns   | 1.000 | 155,264 B |
| TokenizeAccented     | 500,803.37 ns  | ±265,484.78 ns| 14,552.117 ns| 4.833 | 431,592 B |
| TokenizeMixedUnicode | 220,398.04 ns  | ±39,423.19 ns | 2,160.918 ns | 2.127 | 182,864 B |
| NormalizeAsciiOnly   | 44.27 ns       | ±43.52 ns     | 2.385 ns     | 0.000 | -         |
| NormalizeAccented    | 1,138.76 ns    | ±991.57 ns    | 54.351 ns    | 0.011 | 648 B     |

Read at this scale (roughly): ASCII-only input tokenizes ~5× faster than input requiring
accent removal, and allocations scale accordingly. Absolute numbers will differ on any other
machine.

## Reproduce

```bash
dotnet run --project bench/LexiSharp.Benchmarks -c Release
```

Reports land in `BenchmarkDotNet.Artifacts/results/` (CSV, HTML, GitHub-flavored markdown).
For stable numbers use the default (non-`ShortRun`) job configuration and publish the full
report, not a summary.
