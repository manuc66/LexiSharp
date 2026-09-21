# LexiSharp.Eval — corpus reference evaluation

Offline evaluation harness that runs LexiSharp's retrieval engines against a **real, public
corpus** — [NFCorpus](https://www.cl.uni-heidelberg.de/statnlpgroup/nfcorpus/) via the official
BEIR mirror — and reports standard IR metrics (nDCG@10, MAP@10, MRR@10, R@10).

This is the credibility check the unit/FsCheck suites cannot provide: it proves the ranking
engines produce *sane, comparable* rankings on genuine documents and queries, and lets you
compare configs (BM25 variants, TF-IDF, query likelihood, hybrids) head to head.

## Usage

```bash
dotnet run --project bench/LexiSharp.Eval -c Release          # full evaluation (323 test queries)
dotnet run --project bench/LexiSharp.Eval -c Release -- --limit 5   # smoke run
dotnet run --project bench/LexiSharp.Eval -c Release -- --data /path/to/dir   # custom data dir
```

- The dataset (~2.4 MB) is downloaded on first run into `bench/LexiSharp.Eval/data/`
  (gitignored) from the BEIR public mirror and verified against its published md5
  (`a89dba18a62ef92f7d323ec890a0d38d`).
- It uses the **test** split only (`qrels/test.tsv`), 323 queries with graded relevance.
- Metrics are computed with `LexiSharp.Ranking.RetrievalMetrics` (nDCG uses the graded qrel
  scores, MAP/MRR/R use binary relevance).

## Results (NFCorpus, k=10, 323 test queries)

| Config                            | nDCG@10 | MAP@10 | MRR@10 |  R@10 |
|-----------------------------------|--------:|-------:|-------:|------:|
| BM25 (k1=1.5, b=0.75)             |   0.308 |  0.222 |  0.516 | 0.146 |
| BM25 (k1=1.2, b=0.75)             |   0.306 |  0.220 |  0.516 | 0.143 |
| TF-IDF                            |   0.248 |  0.171 |  0.399 | 0.128 |
| QueryLikelihood (λ=0.2)           |   0.288 |  0.205 |  0.485 | 0.132 |
| Hybrid BM25+QL (weighted)         |   0.308 |  0.222 |  0.516 | 0.146 |
| Hybrid BM25+QL (RRF)              |   0.300 |  0.214 |  0.500 | 0.141 |

## Reference

- **BEIR paper** — Thakur et al. 2021, *BEIR: A Heterogeneous Benchmark for Zero-shot
  Evaluation of Information Retrieval Models*: BM25 nDCG@10 = **0.325** on NFCorpus
  (Table 2). <https://arxiv.org/abs/2104.08663>
- LexiSharp's default tokenizer lowercases, folds diacritics and splits on
  non-alphanumerics, **but does not stem**. Absolute scores are therefore expected to sit
  below the stemming baselines (0.308 vs 0.325 for BM25); the *relative* ordering of the
  configs above is the meaningful signal.
- `BM25 tuned` (shown at run time) tunes k1/b **in-sample** on the very queries being scored —
  an oracle, not a fair baseline; it is reported only to exercise
  `Bm25ParameterTuner` end to end.

## Decisions worth knowing

- Not part of CI: it downloads data over the network, so it lives outside the test suite and
  out of the `ci.yml` gates. The `LexiSharp.Benchmarks` project (BenchmarkDotNet) stays a
  *performance* harness; this one is a *quality* harness.
- Since it only depends on the core library and the BCL, it reproduces anywhere `dotnet`
  runs and no dataset code is shipped in the core package.