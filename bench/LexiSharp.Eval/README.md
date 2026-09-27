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

The three md5 values are the ones published in the BEIR README, so the zips this harness
verifies are the same artifacts BEIR distributes. The mirror is deliberate: BEIR datasets are
re-hosted on Hugging Face, and re-hosts have not always matched the original content, which
would silently turn a pinned hash into a different experiment.

## Dataset licenses

**LexiSharp.Eval grants no license over these datasets and redistributes none of them.** The
harness downloads a zip at run time, indexes it in memory, and reports metrics. Attribution and
the right to use the data remain the dataset owner's terms — which is also what the BEIR project
itself says:

> we just downloaded and prepared public datasets. We only distribute these datasets in a specific
> format, but we do not vouch for their quality or fairness, **or claim that you have license to
> use the dataset**. It remains the user's responsibility to determine whether you as a user have
> permission to use the dataset under the dataset's license and to cite the right owner of the
> dataset.
> — [BEIR README, Disclaimer](https://github.com/beir-cellar/beir#beers-disclaimer)

BEIR's own code is Apache-2.0; that license covers the repository, not the data it repackages.

| Dataset | Queries + qrels | Documents | Where the terms were read |
|---------|----------------|-----------|---------------------------|
| SciFact | CC BY 4.0 | **ODC-By 1.0** (abstracts from Semantic Scholar S2ORC) | [`allenai/scifact` LICENSE.md](https://github.com/allenai/scifact/blob/master/LICENSE.md) |
| NFCorpus | **unconfirmed** | **unconfirmed** | dataset homepage states no data license |
| ArguAna | **unconfirmed** | **unconfirmed** | dataset homepage unreachable (HTTP 403) at time of writing |

Two things worth stating rather than glossing over:

- **SciFact's terms are split, and not uniformly CC BY.** The claims and their relevance
  judgments are CC BY 4.0; the documents are abstracts from S2ORC under ODC-By 1.0, a *data*
  license that requires attribution and is not a content license.
- **The Hugging Face `BeIR/*` dataset cards declare `cc-by-sa-4.0` for all three.** That
  contradicts the SciFact primary source above, so treat those cards as a convenience rather than
  as the terms. If you need certainty for NFCorpus or ArguAna, ask the dataset owners or open an
  issue on [beir-cellar/beir](https://github.com/beir-cellar/beir/issues) — the two rows marked
  *unconfirmed* were not resolvable from public pages.

Because a measured score is a fact about an experiment rather than a derivative work of the
corpus, publishing the metrics above does not make LexiSharp a derivative of any dataset. If you
build a tool that *redistributes* corpus text (a vendored sample, a derived golden-master file),
these terms become your problem to solve — which is why the reference corpus planned for the
benchmark CLI is hand-built and owned by this project rather than sliced out of BEIR.

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
dotnet run --project bench/LexiSharp.Eval -c Release -- --stem porter       # English stemming
dotnet run --project bench/LexiSharp.Eval -c Release -- --data /path/to/dir  # custom data dir
```

- Datasets live under `bench/LexiSharp.Eval/data/<name>/` (gitignored).
- Stemming is **off by default**, so every table below is the unstemmed baseline and stays
  comparable with the numbers already published here. `--stem porter` swaps the shared tokenizer
  for one carrying `LexiSharp.Linguistics.PorterStemmer`; the harness prints which tokenization
  a run used, and the effect is measured in [Stemming](#stemming).
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

Default tokenizer, **no stemming** — see [Stemming](#stemming) for the same run with
`--stem porter`.

| Config                             | nDCG@10 | MAP@10 | MRR@10 |  R@10 |
|------------------------------------|--------:|-------:|-------:|------:|
| BM25 (k1=1.5, b=0.75)              |   0.308 |  0.222 |  0.516 | 0.146 |
| BM25 (k1=1.2, b=0.75)              |   0.306 |  0.220 |  0.516 | 0.143 |
| TF-IDF                             |   0.248 |  0.171 |  0.399 | 0.128 |
| QueryLikelihood (λ=0.2)            |   0.288 |  0.205 |  0.485 | 0.132 |
| Hybrid BM25+QL (weighted)          |   0.308 |  0.222 |  0.516 | 0.146 |
| Hybrid BM25+QL (RRF)               |   0.300 |  0.214 |  0.500 | 0.141 |
| BM25F (unweighted)                 |   0.296 |  0.211 |  0.484 | 0.141 |
| BM25F (title 2.0)                  |   0.296 |  0.210 |  0.484 | 0.140 |
| BM25F (title 4.0)                  |   0.296 |  0.211 |  0.492 | 0.139 |
| Dense multilingual-e5-small        |   0.304 |  0.216 |  0.502 | 0.144 |
| Hybrid BM25+Dense (weighted)       |   0.323 |  0.230 |  0.534 | 0.156 |
| Hybrid BM25+Dense (RRF)            |   0.333 |  0.239 |  0.550 | 0.160 |
| BM25 (top100)+CrossRerank          |   0.337 |  0.246 |  0.558 | 0.153 |
| QL (top100)+CrossRerank            |   0.335 |  0.243 |  0.557 | 0.151 |
| Hybrid RRF (top100)+CrossRerank    |   0.346 |  0.250 |  0.573 | 0.158 |

**The corpus is indexed with a real `title` text field**, not as a concatenated string, so the
BM25F rows have a field to weigh. This leaves every non-field-aware row bit-identical — the flat
view is the union of the fields, so BM25 sees the same tokens at the same frequencies and lengths,
and only their order changes, which it does not read. `FieldSplitInvarianceTests` pins that
property, so a future change that broke it would fail the suite rather than quietly rewrite these
tables.

**Proximity rows** (`BM25 + proximity`, three shapes, identical first stage so the difference is
proximity alone): damp at full strength 0.302, damp at quarter 0.308, boost 0.308, against BM25's
0.308. Neutral to slightly negative. An earlier version of `ProximityReranker` reported 0.298 here
because its decay was unbounded — that number was the bug, not the technique; see the floor note in
the main README.

**The BM25F rows are a negative result, and a correction.** On this corpus BM25F sits *below* BM25
(0.296 vs 0.308) and the title weight moves nothing at all.

The honest reading is that these rows compare **default parameters**, not tuned ones. On the
reference corpus, tuning settles it: `bm25f-tuned` reaches **0.8812 nDCG@5, identical to
`bm25-tuned`'s 0.8812**, against BM25F's untuned 0.8189. So the un-tuned gap was BM25F's default
k1=1.2 being a worse fit for a 42-document corpus than BM25's k1=1.5 — **not** a per-field length
term doing something useful. There is no measured case in this repository where BM25F beats BM25
after both are tuned.

Which leaves the ArguAna 0.344 as an **untested** claim, not a result: it too is default-vs-default,
and whether it survives a tuned BM25 was not measured, because a grid over 1406 queries did not fit
the compute available here. Do not quote it as a length-term advantage.

The dense retriever lands at BM25 level on its own (0.304 vs 0.308), and fusing it with BM25
**beats both the dense-only and the lexical-only engines** (0.323–0.333), above the 0.325 BM25
reference from the BEIR paper — the hybrid payoff the `LexiSharp.Hybrid` layer is built for.
The cross-encoder improves every first-stage it is stacked on (+2.9 points over BM25, +4.7
over QL), matching the BEIR paper's finding that BM25+CE outperforms BM25 on most datasets;
**BM25+Dense RRF followed by cross-encoder reranking (0.346) is the best configuration** —
roughly +2.1 points over BEIR's published BM25 baseline and +1.3 over the best non-CE fusion.

## Results (SciFact, k=10, 300 test queries)

Default tokenizer, **no stemming** — see [Stemming](#stemming) for the same run with
`--stem porter`.

| Config                             | nDCG@10 | MAP@10 | MRR@10 |  R@10 |
|------------------------------------|--------:|-------:|-------:|------:|
| BM25 (k1=1.5, b=0.75)              |   0.662 |  0.619 |  0.629 | 0.781 |
| TF-IDF                             |   0.345 |  0.288 |  0.295 | 0.511 |
| QueryLikelihood (λ=0.2)            |   0.622 |  0.582 |  0.593 | 0.727 |
| Hybrid BM25+QL (RRF)               |   0.644 |  0.601 |  0.612 | 0.759 |
| BM25F (unweighted)                 |   0.662 |  0.616 |  0.626 | 0.791 |
| BM25F (title 2.0)                  |   0.665 |  0.623 |  0.634 | 0.780 |
| BM25F (title 4.0)                  |   0.664 |  0.621 |  0.632 | 0.783 |
| BM25 tuned (k1=1.5, b=1)           |   0.664 |  0.619 |  0.630 | 0.789 |

BM25 at 0.662 vs the 0.665 BEIR reference — within 0.5 % on a dataset whose claims use exact
terminology, so the stemming gap (large on NFCorpus, see [Stemming](#stemming)) nearly disappears
here; the tuned k1=1.5/b=1 operating point reproduces the reference at 0.664.

SciFact is the friendliest case for a title weight in this whole harness — BM25F at title 2.0
reaches 0.665, level with the published BM25. It is still +0.003 over BM25's 0.662 on 300 queries,
which is not a result; treat it as "weighting did not hurt here", not as "weighting helps".

## Results (ArguAna, k=10, 1406 test queries)

Default tokenizer, **no stemming** — see [Stemming](#stemming) for the same run with
`--stem porter`.

| Config                             | nDCG@10 | MAP@10 | MRR@10 |  R@10 |
|------------------------------------|--------:|-------:|-------:|------:|
| BM25 (k1=1.5, b=0.75)              |   0.289 |  0.187 |  0.187 | 0.611 |
| BM25 (k1=1.2, b=0.75)              |   0.283 |  0.184 |  0.184 | 0.597 |
| TF-IDF                             |   0.008 |  0.005 |  0.005 | 0.016 |
| QueryLikelihood (λ=0.2)            |   0.227 |  0.147 |  0.147 | 0.483 |
| Hybrid BM25+QL (weighted)          |   0.289 |  0.187 |  0.187 | 0.611 |
| Hybrid BM25+QL (RRF)               |   0.257 |  0.167 |  0.167 | 0.545 |
| BM25F (unweighted)                 |   0.344 |  0.231 |  0.231 | 0.695 |
| BM25F (title 2.0)                  |   0.344 |  0.231 |  0.231 | 0.698 |
| BM25F (title 4.0)                  |   0.340 |  0.227 |  0.227 | 0.696 |

**The largest BM25F margin anywhere in this harness, and not because of the weighting — but also not
established as a real gain.** 0.344 against BM25's 0.289 on 1406 queries. Two reasons to hold it
loosely. The three rows say the weighting is not the cause: unweighted, title 2.0 and title 4.0 are
within 0.004 of each other, and the heaviest weight is the worst. And it is a **default-vs-default**
comparison — on the reference corpus, tuning closed exactly this kind of gap to the digit. Whether a
tuned BM25 also reaches 0.344 here is unmeasured.

ArguAna is also the corpus least like ordinary search — counter-argument retrieval, very long
queries, a `title` that is a topic phrase rather than a headline. One more caveat on reading that
0.344: **each ArguAna test query has exactly one relevant document** (the counter-argument), so
Recall is quantized and nDCG is far easier to move than on a corpus with a dozen relevant documents
per query. A scorer that gets the one right document higher gains a lot; one that reshuffles the
bottom gains nothing.

BM25 over the full 1406-query test split lands at **0.289 against the 0.315 BEIR reference**, so
ArguAna sits **8 % below** it — the largest gap of the three corpora, and the opposite of what
this section used to claim. Correction: the row here used to read 0.320 and was described as
"slightly above" the reference. That number is not reproducible on the current code — `HEAD`
without any of the changes on this branch produces the same 0.289 over the full split — while
`--limit 50`, which evaluates only the first 50 test queries, produces 0.321. The published row
therefore looks like a subset run labelled as a full-split one. The table above is the full
6-config, full-split run the section describes; reproduce it with
`dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset arguana --no-tuned`.

The earlier reading of the cross-dataset pattern — that the gaps were (k1, b)-choice and
tokenization variance rather than a systematic engine deficit — does not survive this, and the
[stemming measurement](#stemming) narrows it further: turning the stemmer on puts NFCorpus and
SciFact on or above their references while ArguAna stays 8 % below, so the analyzer is not what
ArguAna is missing. These runs locate the gap, they do not explain it. Nothing here sweeps the
configurations that were not run — stop words, n-grams, or a k1/b grid beyond the two rows
above — and no claim is made about why ArguAna behaves differently.

ArguAna queries are whole argument texts (~200+ tokens), which makes every query score a large
share of the 8674 single-claim documents. In the run above BM25 takes 117 s for 1406 queries
(~83 ms/query) against under 2 ms/query on NFCorpus — so the oracle tuning grid adds hours there,
which is why `--no-tuned` is the practical default and why the table above is a single
full-split run rather than a sweep. Treat that 83 ms as one machine's wall clock, not a
benchmark.

## Stemming

The core ships `LexiSharp.Linguistics.PorterStemmer` (Porter, 1980 — English only, no
dependency, opt-in). `--stem porter` hands the shared tokenizer to it; stemming is off by
default, so every table above is the unstemmed baseline. Same corpora, same queries, same
`--no-tuned` flags, nDCG@10:

| Config                    | NFCorpus | +stem | SciFact | +stem | ArguAna | +stem |
|---------------------------|---------:|------:|--------:|------:|--------:|------:|
| BM25 (k1=1.5, b=0.75)     |    0.308 | **0.322** |   0.662 | **0.687** |   0.289 | 0.279 |
| BM25 (k1=1.2, b=0.75)     |    0.306 | **0.321** |   0.660 | **0.687** |   0.283 | 0.272 |
| TF-IDF                    |    0.248 | **0.257** |   0.345 | **0.362** |   0.008 | 0.007 |
| QueryLikelihood (λ=0.2)   |    0.288 | **0.298** |   0.622 | **0.648** |   0.227 | 0.212 |
| Hybrid BM25+QL (weighted)  |    0.308 | **0.322** |   0.662 | **0.687** |   0.289 | 0.279 |
| Hybrid BM25+QL (RRF)       |    0.300 | **0.310** |   0.644 | **0.669** |   0.257 | 0.244 |
| BEIR BM25 reference       |    0.325 |    —    |   0.665 |    —    |   0.315 |   —   |

**Stemming helps on two corpora and hurts on the third.** On NFCorpus every config improves and
BM25 goes 0.308 → 0.322, taking the gap to BEIR's 0.325 from −5.2 % to −0.9 %; on SciFact
0.662 → 0.687 puts it *above* the 0.665 reference (R@10 0.781 → 0.818). On ArguAna every config
*loses* ground — BM25 0.289 → 0.279, QL 0.227 → 0.212 — so stemming does not explain that
corpus's gap, it widens it. MAP@10, MRR@10 and R@10 move with nDCG@10 in all three columns.

That three-way split is the argument for keeping stemming **opt-in** rather than defaulting it:
the right choice is corpus-dependent, and this harness cannot say which regime your data is in.
ArguAna's queries are whole arguments, so nearly every term is a content word that gets folded
onto a shared stem — the case where over-stemming has the most to lose and the least to gain.

What this does **not** establish: the indexing and querying cost of stemming is not measured
here — the harness reports wall-clock time per config, but single runs are not timing evidence
(see [BENCHMARKS.md](../../BENCHMARKS.md)). Nor is anything measured on non-English text, on
precision-oriented workloads, on the dense and cross-encoder lanes (they tokenize independently
of the lexical index), or on the reason ArguAna regresses. The [core README](../../README.md)
repeats the scope: Porter's own status text calls the algorithm "slightly inferior to the Snowball
English or Porter2 stemmer", and it over-stems by design (`relate` and `relational` both become
`relat`).

## Reference

- **BEIR paper** — Thakur et al. 2021, *BEIR: A Heterogeneous Benchmark for Zero-shot
  Evaluation of Information Retrieval Models*: BM25 nDCG@10 = **0.325** (NFCorpus), **0.665**
  (SciFact), **0.315** (ArguAna) in Table 2. <https://arxiv.org/abs/2104.08663>
- The default tokenizer lowercases, folds diacritics and splits on non-alphanumerics but
  **does not stem**, and that is the whole of the NFCorpus gap: `--stem porter` takes it from
  0.308 to 0.322 against a 0.325 reference, and from 0.662 to 0.687 on SciFact. It is not the
  whole story everywhere — on ArguAna stemming costs a point — so it stays opt-in, the tables
  above stay unstemmed, and the *relative* ordering of the configs is the meaningful signal.
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