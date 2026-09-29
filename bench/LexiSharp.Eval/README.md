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

# Scored the way the published figures were — see "Comparing against a published number":
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset nfcorpus --no-tuned \
  --analyzer english --ndcg-gain linear --reference-bm25 0.9,0.4 --query-term-frequency

# The gate: replays every pinned configuration, exits 1 on drift.
dotnet run --project bench/LexiSharp.Eval -c Release -- --verify-reference

# Cheap: what the index holds, against the reference index. No scoring.
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset all --fingerprint --analyzer english
```

- Datasets live under `bench/LexiSharp.Eval/data/<name>/` (gitignored).
- Stemming is **off by default**, so every table below is the unstemmed baseline and stays
  comparable with the numbers already published here. `--stem porter` swaps the shared tokenizer
  for one carrying `LexiSharp.Linguistics.PorterStemmer`; the harness prints which analysis
  a run used, and the effect is measured in [Stemming](#stemming).
- **The default tables and the published comparison are not the same table.** The defaults are what
  the library does out of the box; a number compared with a published one needs the analysis, the
  parameters, the gain convention and the repeated-term rule the reference used. Both are below.
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

## Comparing against a published number

A BM25 figure is only comparable with a run that shares its **analysis**, its **parameters** and its
**task conventions**. Change any of the three and the difference is a statement about the
configuration, not about the ranking engine — which is a mistake this harness was built to make easy
to see rather than to hide.

The three things that differ from a Lucene/Anserini run, and the flags that align them:

| | published baseline | this harness by default | flag |
|---|---|---|---|
| analysis | Porter stemming + a 33-word function-word list, one-character terms kept | lowercase, diacritics folded, no stemming, stop words kept | `--analyzer english` |
| parameters | the stack's own defaults, k1=0.9 b=0.4 | k1=1.5 b=0.75 (and 1.2) | `--reference-bm25 0.9,0.4` |
| nDCG gains | gain = rel | `2^rel − 1` | `--ndcg-gain linear` |
| repeated query terms | scored once per occurrence | deduplicated first | `--query-term-frequency` |
| task | the document whose id equals the query id is dropped | kept | `--exclude-query-doc` |

The gain conventions coincide on binary relevance (`2¹ − 1 = 1`), so that row only matters on
NFCorpus, the one graded corpus here: 0.3080 exponential against 0.3071 linear for the same run.

**`--query-term-frequency` is what closed the ArguAna gap, and it is worth nothing elsewhere.**
A published BM25 measurement scores one clause per query-token occurrence, so a term repeated in the
query multiplies its weight; this library deduplicates first, which is the natural reading of a bag
of words and the default it keeps. Where queries are short the two are identical to four decimals
(NFCorpus 0.3215 either way, SciFact 0.6788 either way). Where a query *is* a document they are not:
ArguAna's every test query is a whole ~200-word argument whose content words repeat, and counting
them takes nDCG@10 from **0.2197 to 0.2902** at k1=0.9/b=0.4 — and to **0.4061** at k1=3.0, against
the **0.3970** published. That was the whole of the 0.11 that this repository had written down as an
unexplained deficit, and it was a scoring rule, not a parameter.

**`--exclude-query-doc` is worth far less on ArguAna than it looks, and the measurement is in
`reference/pinned.json`.** 1298 of the 1406 ArguAna test queries have a query id that is a document
id, and their own document is a near-duplicate that ranks first, so dropping it looks like it should
be worth a lot. Measured: **0.2852 without, 0.2864 with, i.e. +0.0012.** The reason is that the
relevant document is already inside the top 10 in every one of the 869 cases where it is retrievable
at all (median rank 3), and 537 queries have no relevant document in any page. Excluding the
self-match promotes the gold document by one rank in 810 queries, and the page refills from rank 11,
which is almost never the gold document. An earlier estimate of +0.095 for this flag was an artifact
of dropping the self-document from an already-cut 10-document page — leaving a hole rather than
refilling it — and it was wrong.


`--fingerprint` reports what the index holds — documents, non-empty documents, total terms — next to
the counts an independent implementation's index of the same corpus holds. Those are integers, and
they say something a score cannot: whether the two runs indexed the same thing. Measured, with the
reference analysis: NFCorpus 655,155 terms against 637,485 (2.8%), SciFact 850,694 against 838,128
(1.5%), ArguAna 980,418 against 969,528 (1.1%). With the default analyzer the same corpora read
838,401 / 1,111,245 / 1,407,061 — 31% to 45% larger, which is most of the apparent gap in the tables
below.

## The regression net

`--verify-reference` replays every configuration in [`reference/pinned.json`](reference/pinned.json)
and exits 1 on any drift, so it can gate a build. The file holds two kinds of number, and they are
not interchangeable:

- **Regression pins** — the value this repository must keep producing, at a named configuration
  (analyzer, gain convention, BM25 parameters, query-document exclusion, query count). The
  tolerance is tight (±0.002) because these are the assertions: they catch a change to scoring,
  candidate generation, tokenization or the metric.
- **Parity figures** — somebody else's published number, with its source and a note on whether the
  parameters matched. Evidence, not assertions. A parity figure across *different* BM25 parameters
  is a different claim from a like-for-like one, and the file records which is which.

```bash
dotnet run --project bench/LexiSharp.Eval -c Release -- --verify-reference
```

Re-recording is deliberately not a flag: `--write` is accepted and reports that it did not write.
Recording is a separate, reviewed act — the same reasoning as `lexisharp baseline` in the CLI, and
for the same reason. Read what moved, and why, before re-recording.

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
| BM25+ (delta=1.0)                  |   0.302 |  0.217 |  0.505 | 0.143 |
| BM25L (delta=0.5)                  |   0.305 |  0.218 |  0.508 | 0.144 |
| BM25 tuned (k1=2, b=0.5)           |   0.311 |  0.224 |  0.526 | 0.146 |
| BM25+ tuned (k1=2, b=0.5, δ=0)     |   0.311 |  0.224 |  0.526 | 0.146 |
| BM25L tuned (k1=2, b=0.5, δ=0)     |   0.311 |  0.224 |  0.526 | 0.146 |
| Dense multilingual-e5-small        |   0.304 |  0.216 |  0.502 | 0.144 |
| Hybrid BM25+Dense (weighted)       |   0.323 |  0.230 |  0.534 | 0.156 |
| Hybrid BM25+Dense (RRF)            |   0.333 |  0.239 |  0.550 | 0.160 |
| BM25 (top100)+CrossRerank          |   0.337 |  0.246 |  0.558 | 0.153 |
| QL (top100)+CrossRerank            |   0.335 |  0.243 |  0.557 | 0.151 |
| Hybrid RRF (top100)+CrossRerank    |   0.346 |  0.250 |  0.573 | 0.158 |

**The same corpus, scored the way the published figures were** — `--analyzer english --ndcg-gain
linear --reference-bm25 0.9,0.4 --query-term-frequency`. This is the table to read when asking
"does this beat a published BM25", and the table above is the one to read when asking what the
library's own defaults do:

| Config                             | nDCG@10 | MAP@10 | MRR@10 |  R@10 |
|------------------------------------|--------:|-------:|-------:|------:|
| BM25 (k1=1.5, b=0.75)              |   0.327 |  0.238 |  0.535 | 0.155 |
| BM25 (k1=1.2, b=0.75)              |   0.323 |  0.234 |  0.528 | 0.153 |
| BM25 (k1=0.9, b=0.4) *             |   0.322 |  0.234 |  0.524 | 0.153 |
| TF-IDF                             |   0.274 |  0.190 |  0.445 | 0.140 |
| QueryLikelihood (λ=0.2)            |   0.303 |  0.217 |  0.497 | 0.146 |
| Hybrid BM25+QL (weighted)          |   0.327 |  0.238 |  0.535 | 0.155 |
| Hybrid BM25+QL (RRF)               |   0.314 |  0.226 |  0.514 | 0.149 |
| BM25F (unweighted)                 |   0.315 |  0.227 |  0.514 | 0.152 |
| BM25F (title 2.0)                  |   0.317 |  0.228 |  0.517 | 0.152 |
| BM25F (title 4.0)                  |   0.318 |  0.230 |  0.517 | 0.151 |
| BM25+ (δ=1.0)                      |   0.317 |  0.230 |  0.518 | 0.148 |
| BM25L (δ=0.5)                      |   0.319 |  0.231 |  0.519 | 0.152 |
| BM25 + proximity (damp, s=0.25)    |   0.328 |  0.238 |  0.537 | 0.155 |
| BM25 + proximity (boost, s=1)      |   0.327 |  0.237 |  0.536 | 0.155 |
| **Published reference BM25**       | **0.3218** | — | — | — |

\* the row marked is the published operating point, the one to compare with the reference. The
0.327 rows are this library's own default k1/b under the reference's analysis, and they are above
the reference; the 0.322 row is the reference's own k1/b, and it is 0.0003 below the published
0.3218. The gap between the two default tables above (0.308 against 0.327 for the same scorer) is
analysis, parameters and gain convention, and nothing else.

**The corpus is indexed with a real `title` text field**, not as a concatenated string, so the
BM25F rows have a field to weigh. This leaves every non-field-aware row bit-identical — the flat
view is the union of the fields, so BM25 sees the same tokens at the same frequencies and lengths,
and only their order changes, which it does not read. `FieldSplitInvarianceTests` pins that
property, so a future change that broke it would fail the suite rather than quietly rewrite these
tables.

**The BM25+ / BM25L rows are the tuned-vs-tuned comparison, and the numbers do not flatter the
variants.** Each `δ` is now fitted by `Bm25PlusParameterTuner` / `Bm25LParameterTuner` over the full
`k1 × b × δ` product (125 points) on the same queries, on nDCG, as `BM25 tuned` — previously the
variants sat at a fixed paper `δ` while BM25's `(k1, b)` were fitted, which is not a comparison of
the formulas.

| Corpus | `BM25 tuned` | `BM25+ tuned` | `BM25L tuned` | unfloored baseline | `DeltaHelped` |
|---|---|---|---|---|---|
| NFCorpus | 0.311 (k1=2, b=0.5) | 0.311 (δ=0) | 0.311 (δ=0) | 0.311 | false, both |
| SciFact | 0.664 (k1=1.5, b=1) | 0.666 (δ=0.25) | 0.668 (δ=0.25) | **0.664** | true, both |

On NFCorpus the three tuners independently select identical parameters and identical scores, so
those three rows are one result reported three times: the δ search picks no bound and hands back
BM25. On SciFact δ = 0.25 wins, and the **unfloored baseline the harness prints is 0.664 — exactly
`BM25 tuned`'s score**, which confirms at corpus scale that both variants reduce to BM25 at δ = 0.
That makes the +0.002 / +0.004 attributable to δ rather than to a wider grid, since both searches
cover the same 25 `(k1, b)` pairs. It is still 300 queries, binary relevance, best-of-125, fitted
in-sample on the queries it is scored on: an upper bound, not a result. ArguAna was run
`--no-tuned`, so it has no variant rows at all rather than a half-affordable one.

**Proximity rows** (`BM25 + proximity`, three shapes, identical first stage so the difference is
proximity alone): damp at full strength 0.302, damp at quarter 0.308, boost 0.308, against BM25's
0.308. Neutral to slightly negative. An earlier version of `ProximityReranker` reported 0.298 here
because its decay was unbounded — that number was the bug, not the technique; see the floor note in
the main README.

**The BM25F rows are a negative result, and a correction.** On this corpus BM25F sits *below* BM25
(0.296 vs 0.308) and the title weight moves nothing at all.

The honest reading is that these rows compare **default parameters**, not tuned ones. On the
reference corpus, tuning settles it: `bm25f-tuned` reaches **0.8867 nDCG@5 against
`bm25-tuned`'s 0.8978**, from BM25F's untuned 0.8189. So the un-tuned gap was BM25F's default
k1=1.2 being a worse fit for a 42-document corpus than BM25's k1=1.5 — **not** a per-field length
term doing something useful. There is no measured case in this repository where BM25F beats BM25
after both are tuned.

Those two figures were **equal at 0.8812** in an earlier version of this file. The equality was an
artifact of the objective, not a property: the run tuned on F1@5, which is flat on the reference
corpus (0.3588 for every configuration), so neither tuner had any signal to fit and both took the
first grid point their tie-break reached. Re-fitted on nDCG@5 — the metric they are reported in —
BM25F tuned *loses* to BM25 tuned by 0.011. The conclusion is unchanged and better supported.

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
| BM25+ (delta=1.0)                  |   0.656 |  0.612 |  0.622 | 0.778 |
| BM25L (delta=0.5)                  |   0.655 |  0.609 |  0.620 | 0.782 |
| BM25 tuned (k1=1.5, b=1)           |   0.664 |  0.619 |  0.630 | 0.789 |
| BM25+ tuned (k1=2, b=1, δ=0.25)    |   0.666 |  0.620 |  0.631 | 0.792 |
| BM25L tuned (k1=2, b=1, δ=0.25)    |   0.668 |  0.623 |  0.633 | 0.792 |

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
| BM25+ (delta=1.0)                  |   0.243 |  0.156 |  0.156 | 0.519 |
| BM25L (delta=0.5)                  |   0.249 |  0.161 |  0.161 | 0.528 |

**The BM25+ / BM25L rows here are the worst the variants do anywhere, and the one measurement in
this repository where the missing δ knob is most likely to matter.** At a fixed paper δ they lose
0.046 and 0.040 against BM25 — roughly 15 % relative, the same order as the 13–17 % that 9bb7c92
retracted, but for a different reason and on a different corpus. That retraction was a
transcription error on NFCorpus and SciFact; these rows are correctly transcribed and simply lose.
ArguAna is also the corpus where a δ floor should plausibly do most work and least easily be
recovered: a 125-point search over 1406 queries whose documents are whole arguments, on a single
relevant document per query, is hours of compute, so **this run was `--no-tuned` and the variant
rows are untuned-vs-untuned only.** Whether tuning δ closes a 0.046 gap here is **unmeasured**, and
the NFCorpus/SciFact results do not transfer — SciFact was the one corpus where δ helped at all.
Do not read these rows as the variants' verdict; read them as untuned, which is all they are.

**The largest BM25F margin anywhere in this harness, and not because of the weighting — but also not
established as a real gain.** 0.344 against BM25's 0.289 on 1406 queries, both under the library
defaults. Hold this one loosely for a second reason as well: the default table is no longer the
comparable one (see above), so this margin is a default-versus-default comparison in a table whose
own caveats apply. Two reasons to hold it
loosely. The three rows say the weighting is not the cause: unweighted, title 2.0 and title 4.0 are
within 0.004 of each other, and the heaviest weight is the worst. And it is a **default-vs-default**
comparison — on the reference corpus, tuning moved this kind of gap by 0.011, and on NFCorpus and
SciFact it closed. Whether a tuned BM25 also reaches 0.344 here is unmeasured.

ArguAna is also the corpus least like ordinary search — counter-argument retrieval, very long
queries, a `title` that is a topic phrase rather than a headline. One more caveat on reading that
0.344: **each ArguAna test query has exactly one relevant document** (the counter-argument), so
Recall is quantized and nDCG is far easier to move than on a corpus with a dozen relevant documents
per query. A scorer that gets the one right document higher gains a lot; one that reshuffles the
bottom gains nothing.

BM25 over the full 1406-query test split lands at **0.289 under the library defaults**, and this
section used to call that "8 % below the reference" and leave it there. It is not 8 % below: the
0.315 it was compared against is the 2021 paper's figure, the current reference for this index is
**0.3970**, and the deficit has a cause that was measurable and was not a parameter.

| configuration | nDCG@10 |
|---|---:|
| library defaults (the table above) | 0.289 |
| + reference analysis, + query-frequency counting, at k1=0.9/b=0.4 | 0.2902 |
| the same at k1=3.0/b=0.75 | **0.4061** |
| published reference, at k1=0.9/b=0.4 | 0.3970 |

The cause is that this library deduplicates query terms before scoring and the reference counts
one clause per query-token occurrence. On a corpus where every test query is a whole ~200-word
argument, that is worth **+0.0705** on its own. Reproduce it with
`--analyzer english --ndcg-gain linear --query-term-frequency --exclude-query-doc
--reference-bm25 3.0,0.75`. The 0.4061 is at a k1 the reference does not use, so this is not a
like-for-like number either — what it shows is that the deficit was a scoring rule, not a
capability, and that the corpus is sensitive to k1 in the opposite direction to the other two.
A like-for-like ArguAna comparison at the reference's own parameters is **unmeasured** and would
need the k1/b grid this corpus' 1406 long queries make expensive.

Correction that stands: the ArguAna row here used to read 0.320 and was described as "slightly
above the reference". That number is not reproducible — `--limit 50`, which evaluates only the
first 50 test queries, produces 0.321, so the row looked like a subset run labelled as a full-split
one. The table above is the full 6-config, full-split run the section describes; reproduce it with
`dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset arguana --no-tuned`.

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
| Published reference       |    0.325 |    —    |   0.665 |    —    |   0.315 |   —   |

This table switches the **stemmer only**, which is what makes it readable, and it is also the reason
the stemming section used to look like the whole story: the analysis a published measurement was
produced with is a stemmer *and* a stop word list, and only the first half is in this table.
`--analyzer english` turns on both, and reaches 0.327 on NFCorpus and 0.692 on SciFact — above the
published figures, where this table's best is 0.322 and 0.687. The stemmer's own contribution is
therefore worth about +0.014 on NFCorpus and the stop words another +0.005; on ArguAna both cost
something, and the gap there is not analysis at all — see `--query-term-frequency` above.

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
(see [docs/benchmarks.md](../../docs/benchmarks.md)). Nor is anything measured on non-English text, on
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