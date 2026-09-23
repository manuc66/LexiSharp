# Benchmarks

Numbers in this file come from BenchmarkDotNet runs on a single, aging developer machine —
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

## Reproduce

```bash
dotnet run --project bench/LexiSharp.Benchmarks -c Release
# search-only subset (fast):
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- --filter '*Search*'
```

Reports land in `BenchmarkDotNet.Artifacts/results/` (CSV, HTML, GitHub-flavored markdown).
For stable numbers use the default (non-`ShortRun`) job configuration and publish the full
report, not a summary.