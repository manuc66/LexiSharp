# LexiSharp.Eval — corpus reference evaluation

Offline evaluation harness that runs LexiSharp's retrieval engines against **real, public
corpora** from the [BEIR](https://github.com/beir-cellar/beir) family (NFCorpus, SciFact,
ArguAna) and reports standard IR metrics (nDCG@10, MAP@10, MRR@10, R@10).

This is the credibility check the unit/FsCheck suites cannot provide: it proves the ranking
engines produce *sane, comparable* rankings on genuine documents and queries, and lets you
compare configs (BM25 variants, TF-IDF, query likelihood, dense, hybrids) head to head.

## Datasets & published baselines

All numbers are from the BEIR paper (Thakur et al. 2021, Table 2, Pyserini BM25).
Each dataset is downloaded on first use from the BEIR public mirror and its zip is verified
against its published md5.

| Dataset | Zip md5 | Test queries | Relevance | BM25 nDCG@10 (BEIR) |
|---------|---------|--------------|-----------|---------------------|
| NFCorpus | `a89dba18a62ef92f7d323ec890a0d38d` | 323 | graded (3 levels) | 0.325 |
| SciFact  | `5f7d1de60b170fc8027bb7898e2efca1` | 300 | binary | 0.665 |
| ArguAna  | `8ad3e3c2a5867cdced806d6503f29b99` | 1406 | binary | 0.315 |

## Usage

```bash
dotnet run --project bench/LexiSharp.Eval -c Release                          # NFCorpus, full
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset scifact     # SciFact
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset arguana --no-tuned   # ArguAna, skip oracle
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset all --no-tuned       # all datasets
dotnet run --project bench/LexiSharp.Eval -c Release -- --dense               # add dense retrieval (e5-small)
dotnet run --project bench/LexiSharp.Eval -c Release -- --rerank              # add cross-encoder reranking (MiniLM)
dotnet run --project bench/LexiSharp.Eval -c Release -- --rerank-top 50       # rerank depth (default 100)
dotnet run --project bench/LexiSharp.Eval -c Release -- --limit 5            # smoke run
dotnet run --project bench/LexiSharp.Eval -c Release -- --data /path/to/dir  # custom data dir
```

- Datasets live under `bench/LexiSharp.Eval/data/<name>/` (gitignored).
- Evaluation uses the **test** split only (`qrels/test.tsv`), one metric set per dataset
  (nDCG uses the graded qrel scores where present, MAP/MRR/R use binary relevance).
- Metrics are computed with `LexiSharp.Ranking.RetrievalMetrics`.
- `--no-tuned` skips the in-sample k1/b oracle grid: on ArguAna the 1406 long queries make
  the 5×5 grid the dominant cost (the oracle adds ~2 h there on a modest CPU), so the flag is
  the practical default for heavy datasets.

## Dense retrieval (optional, pure .NET)

`--dense` adds a dense retriever and lexical+dense hybrids to the table. Everything runs
in-process with ONNX Runtime — no Python:

- Model: **`intfloat/multilingual-e5-small`** via the official Xenova ONNX export
  (`onnx/model.onnx`, fp32, 384-dim, MIT) + its `sentencepiece.bpe.model` tokenizer.
- Pipeline: SentencePiece tokenization re-mapped to the XLM-R id space (the Microsoft tokenizer
  numbers its pieces consistently one below HuggingFace's — `+1` everywhere except the
  specials, with `0 <s>`, `1 <pad>`, `2 </s>`, `3 <unk>`), `query: `/`passage: ` prefixes
  (required by e5), autoregressive batches, mean pooling over the attention mask, L2
  normalization, then in-memory cosine ranking over the whole corpus.
- First run downloads the model (~470 MB once) into `data/models/` and encodes
  corpus+queries on CPU (threads capped at 4, `--dense-seq <n>` truncates to n tokens/turn,
  default 256, lower = faster); embeddings are cached in `data/<dataset>/`.
- The dense engine is a read-only `ITextSearchEngine` in the harness, so it composes with the
  core's `HybridTextSearchEngine` + `WeightedScoreResultMerger`/`ReciprocalRankFusionMerger`
  exactly like any other engine.

## Cross-encoder reranking (optional, pure .NET)

`--rerank` adds a second-stage cross-encoder that re-scores the top-`--rerank-top` (default
100) candidates of a first-stage engine and reorders them — still in-process with ONNX
Runtime, no Python:

- Model: **`Xenova/ms-marco-MiniLM-L-6-v2`** (official ONNX export, fp32, ~91 MB, Apache-2.0)
  of the MS MARCO passage-ranking MiniLM cross-encoder.
- Pipeline: a minimal BERT WordPiece tokenizer (`vocab.txt`, greedy longest-match with `##`
  continuations), `[CLS] q [SEP] d [SEP]` pairs with `token_type_ids=1` on the document
  segment, batches of 16 padded to the batch max, single `logits` row per pair, ranking by
  raw logit descending. Capped at 4 intra-op threads.
- The reranker wraps any first-stage engine via `RerankTextSearchEngine`, so the table gains
  `BM25+CrossRerank`, `QL+CrossRerank`, and (with `--dense`) `Hybrid RRF+CrossRerank` rows.
- First run downloads the model into `data/models/cross-encoder/`. Reranking is the dominant
  cost of a run: ~6–10 s/query for the 100-candidate depth on NFCorpus, so a full 323-query
  table takes on the order of an hour on a modest CPU; use `--limit` while iterating.
- The reranker scores each candidate's *document text* (query and doc are tokenized as a
  BERT pair); scores are validated against the HuggingFace `CrossEncoder` reference on the
  same query–doc pairs (Spearman ρ ≈ 0.99 on NFCorpus candidates, top-10 overlap ≈ 0.9).

## Results (NFCorpus, k=10, 323 test queries)

| Config                             | nDCG@10 | MAP@10 | MRR@10 |  R@10 |
|------------------------------------|--------:|-------:|-------:|------:|
| BM25 (k1=1.5, b=0.75)              |   0.308 |  0.222 |  0.516 | 0.146 |
| BM25 (k1=1.2, b=0.75)              |   0.306 |  0.220 |  0.516 | 0.143 |
| TF-IDF                             |   0.248 |  0.171 |  0.399 | 0.128 |
| QueryLikelihood (λ=0.2)            |   0.288 |  0.205 |  0.485 | 0.132 |
| Hybrid BM25+QL (weighted)          |   0.308 |  0.222 |  0.516 | 0.146 |
| Hybrid BM25+QL (RRF)               |   0.300 |  0.214 |  0.500 | 0.141 |
| Dense multilingual-e5-small        |   0.304 |  0.216 |  0.502 | 0.144 |
| Hybrid BM25+Dense (weighted)       |   0.323 |  0.230 |  0.534 | 0.156 |
| Hybrid BM25+Dense (RRF)            |   0.333 |  0.239 |  0.550 | 0.160 |
| BM25 (top100)+CrossRerank          |   0.337 |  0.246 |  0.558 | 0.153 |
| QL (top100)+CrossRerank            |   0.335 |  0.243 |  0.557 | 0.151 |
| Hybrid RRF (top100)+CrossRerank    |   0.346 |  0.250 |  0.573 | 0.158 |

The dense retriever lands at BM25 level on its own (0.304 vs 0.308), and fusing it with BM25
**beats both the dense-only and the lexical-only engines** (0.323–0.333), above the 0.325 BM25
reference from the BEIR paper — the hybrid payoff the `LexiSharp.Hybrid` layer is built for.
The cross-encoder improves every first-stage it is stacked on (+2.9 points over BM25, +4.7
over QL), matching the BEIR paper's finding that BM25+CE outperforms BM25 on most datasets;
**BM25+Dense RRF followed by cross-encoder reranking (0.346) is the best configuration** —
roughly +2.1 points over BEIR's published BM25 baseline and +1.3 over the best non-CE fusion.

## Results (SciFact, k=10, 300 test queries)

| Config                             | nDCG@10 | MAP@10 | MRR@10 |  R@10 |
|------------------------------------|--------:|-------:|-------:|------:|
| BM25 (k1=1.5, b=0.75)              |   0.662 |  0.619 |  0.629 | 0.781 |
| TF-IDF                             |   0.345 |  0.288 |  0.295 | 0.511 |
| QueryLikelihood (λ=0.2)            |   0.622 |  0.582 |  0.593 | 0.727 |
| Hybrid BM25+QL (RRF)               |   0.644 |  0.601 |  0.612 | 0.759 |
| BM25 tuned (k1=1.5, b=1)           |   0.664 |  0.619 |  0.630 | 0.789 |

BM25 at 0.662 vs the 0.665 BEIR reference — within 0.5 % on a dataset whose claims use exact
terminology, so the no-stemming gap (visible on NFCorpus) nearly disappears here; the tuned
k1=1.5/b=1 operating point reproduces the reference at 0.664.

## Results (ArguAna, k=10, 1406 test queries)

| Config                             | nDCG@10 | MAP@10 | MRR@10 |  R@10 |
|------------------------------------|--------:|-------:|-------:|------:|
| BM25 (k1=1.5, b=0.75)              |   0.320 |  0.207 |  0.207 | 0.679 |

BM25 over the full 1406-query test split lands at **0.320 vs the 0.315 BEIR reference** —
slightly *above* it. Together with NFCorpus (−5 %) and SciFact (−0.5 %), this shows the
cross-dataset gap is (k1, b)-choice and tokenization variance, not a systematic engine deficit.

ArguAna queries are whole argument texts (~200+ tokens), which makes every query score a large
share of the 8674 single-claim documents: ~190 ms/query on this machine vs ~2 ms/query on
NFCorpus/SciFact — the oracle tuning grid adds about two hours there, which is why `--no-tuned`
is the practical default. The single row above is a full-corpus, full test-split run; the
multi-config table is left to `--no-tuned --limit` smoke runs or a calmer machine.

## Reference

- **BEIR paper** — Thakur et al. 2021, *BEIR: A Heterogeneous Benchmark for Zero-shot
  Evaluation of Information Retrieval Models*: BM25 nDCG@10 = **0.325** (NFCorpus), **0.665**
  (SciFact), **0.315** (ArguAna) in Table 2. <https://arxiv.org/abs/2104.08663>
- LexiSharp's default tokenizer lowercases, folds diacritics and splits on
  non-alphanumerics, **but does not stem** — the dominant cause of the (small, systematic,
  dataset-scaled) gap below the published baselines. On NFCorpus (inflected medical
  vocabulary) it costs ~5 %; on SciFact (exact terminology) it is ~0.5 %. The *relative*
  ordering of the configs is the meaningful signal.
- `BM25 tuned` (shown at run time) tunes k1/b **in-sample** on the very queries being scored —
  an oracle, not a fair baseline; it is reported only to exercise
  `Bm25ParameterTuner` end to end.

## Decisions worth knowing

- Not part of CI: it downloads data over the network, so it lives outside the test suite and
  out of the `ci.yml` gates. The `LexiSharp.Benchmarks` project (BenchmarkDotNet) stays a
  *performance* harness; this one is a *quality* harness.
- The dense path is opt-in (`--dense`) because the first run downloads ~470 MB and encodes on
  CPU for several minutes; it is resource-bounded (4 threads) and cached, so it never
  re-encodes. The reranker is opt-in (`--rerank`) for the opposite reason: it is cheap to
  download (~91 MB) but the ranking itself is compute-heavy and dominates run time.
- Since it only depends on the core library, the BCL and ONNX Runtime (eval project only), no
  dataset or model code is shipped in the core package.