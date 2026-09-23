# Benchmarks

Numbers in this file come from BenchmarkDotNet runs on a single, aging developer machine —
they are **indicative only**, not a performance claim. Run the suite yourself before drawing
any conclusion about your own corpus and hardware.

## Machine

```
BenchmarkDotNet v0.14.0, Manjaro Linux
Intel Core i7-4790 CPU 3.60GHz (Haswell), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.111
```

Two measurement generations are mixed below:

- **Index building & Tokenizer** (older): `Job.ShortRun` (IterationCount=3, WarmupCount=3,
  LaunchCount=1) on runtime `.NET 8.0.30`. Only 3 iterations → wide, noisy error bars; treat
  as order of magnitude. These two tables have not been re-measured on .NET 10 yet.
- **Search** (current, HEAD): default job on runtime `.NET 10.0.11`, measured after the perf
  work merged on `main`.

## Search (`SearchBenchmarks`)

Same synthetic corpus as the index builder: 10,000 documents, 50 words each. Default job,
runtime `.NET 10.0.11`. `Allocated` excludes the corpus itself (index built once before the
loop); it is the per-query allocation (per 1,000 operations in `Gen*` columns):

| Method                          | Mean      | Error     | StdDev    | Allocated |
|-------------------------------- |----------:|----------:|----------:|----------:|
| Bm25Search                      |  2.832 ms | 0.2562 ms | 0.7555 ms |   1.58 KB |
| TfIdfSearch                     |  2.642 ms | 0.1195 ms | 0.3504 ms | 392.06 KB |
| QueryLikelihoodSearch           |  3.666 ms | 0.1621 ms | 0.4779 ms | 392.06 KB |
| BooleanSearch                   |  1.479 ms | 0.0724 ms | 0.2123 ms | 392.06 KB |
| Bm25SearchRunAllQueries         | 27.940 ms | 0.5581 ms | 1.6012 ms |   8.13 KB |

Provenance: `Bm25Search`/`Bm25SearchRunAllQueries` were re-measured after the per-query plan
hoist (`perf: hoist query-level corpus constants...`); `TfIdf`,`QueryLikelihood` and `Boolean`
rows come from the previous optimization pass. A full refresh of every row on the same
runtime is scheduled.

### Before / after the search optimization pass (runtime .NET 10.0.11, same machine)

| Method                          | Before          | After          | Mean Δ   | Alloc Δ   |
|-------------------------------- |----------------:|---------------:|---------:|----------:|
| Bm25Search                      |  6.923 ms / 4.12 MB |  2.832 ms / 1.58 KB | −59 % | −99.96 % |
| TfIdfSearch                     |  6.239 ms / 4.12 MB |  2.642 ms / 392 KB | −58 % | −99.9 %  |
| QueryLikelihoodSearch           |  7.062 ms / 4.12 MB |  3.666 ms / 392 KB | −48 % | −99.99 % |
| BooleanSearch                   |  1.922 ms / 1.07 MB |  1.479 ms / 392 KB | −23 % | −64 %    |
| Bm25SearchRunAllQueries         | 47.421 ms / 20.74 MB | 27.940 ms / 8.13 KB | −41 % | −99.96 % |

Ranking output of every configuration is verified byte-for-byte against the pre-optimization
engine (`QueryPlanParityTests`, 314 tests green), so the speedups come with no quality trade-off.

## Index building (`IndexBenchmarks`)

Runtime `.NET 8.0.30`, `Job.ShortRun` — **to be re-measured on .NET 10**. Building an
`InMemoryTextIndex` from scratch, 50 words per document. Gen0/Gen1/Gen2 columns are GC
collection counts per 1,000 operations (BenchmarkDotNet convention):

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

Runtime `.NET 8.0.30`, `Job.ShortRun` — **to be re-measured on .NET 10**. Tokenizing the
same text under different normalizations. The `Ratio` column is only meaningful within the
`Tokenize*` rows — the `Normalize*` methods operate on a much smaller input:

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
# search-only subset (fast) with the default job:
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- --filter 'Bm25Search|TfIdfSearch|QueryLikelihoodSearch|BooleanSearch'
```

Reports land in `BenchmarkDotNet.Artifacts/results/` (CSV, HTML, GitHub-flavored markdown).
For stable numbers use the default (non-`ShortRun`) job configuration and publish the full
report, not a summary.