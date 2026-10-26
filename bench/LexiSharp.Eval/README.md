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
dotnet run --project bench/LexiSharp.Eval -c Release -- --tune-bm25f          # fit BM25F's k1/b and the field weights
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

# A SQL backend on the same corpus, queries and qrels — see "SQL backends":
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset nfcorpus --no-tuned \
  --backend paradedb --backend-connection "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=lexisharp"

# Learned sparse over vectors produced outside the harness — see "Learned sparse":
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset nfcorpus --no-tuned \
  --ndcg-gain linear --sparse bench/LexiSharp.Eval/data/sparse-vectors
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
- **The run is resumable.** A one-line-per-pair cache under `data/rerank-cache/` (or
  `--rerank-cache <dir>`) is flushed to disk every 1,000 pairs, so an interrupted run restarts
  where it stopped instead of at the first query. The file is named for the corpus md5 and the
  model, because a score depends on both; it is append-only, so a kill during a write costs the
  last buffered pairs and nothing already written; and a truncated tail is skipped with a count
  rather than discarding the file. Every worker started by `--jobs` appends through one lock that
  covers a whole line and the flush it may trigger, so two queries scoring at once cannot splice
  one line into another. A pair that two workers miss at the same moment is written once: the
  second worker's model pass is wasted, and it ranks with the score the first one stored. The
  loader keeps one line per pair and reports repeated and unreadable lines when it opens the file.
  The three reranked rows of one corpus also share candidates:
  on ArguAna **175,420 of 394,621 pair-requests (44 %) were served from an earlier row's passes**,
  and on five NFCorpus queries the second row computed nothing at all. Delete the directory to force
  a re-score.

## SQL backends (optional, needs PostgreSQL)

`--backend` scores one of the PostgreSQL engines on the same corpus, the same test queries and the
same qrels as the table above, through the same metric code at the same depth:

| `--backend` | engine | ranking | needs |
|---|---|---|---|
| `lexical` | `PostgresTextSearchEngine` | `ts_rank_cd` | stock PostgreSQL (the lane installs `unaccent`) |
| `paradedb` | `ParadeDBTextSearchEngine` | `pdb.score`, Tantivy's BM25 | `paradedb/paradedb:pg16` (`pg_search`) |
| `vector` | `PostgresVectorSearchEngine` | cosine over pgvector HNSW | `--dense` (it ranks the cached e5 vectors) |

The row is printed on its own rather than appended to the table: every row above is a different
scorer over one shared in-memory index, and these engines own both their index and their ranking
function. The lane builds a table named `lexisharp_eval_<dataset>` and drops it when the run ends,
so it never reads or empties a table an application uses.

`--backend-bm25 0.9,0.4` sets ParadeDB's k1/b. `pg_search` takes them as per-field tokenizer
arguments, so the index is built at that operating point rather than re-weighted afterwards; omitted,
Tantivy's defaults (k1=1.2, b=0.75) apply. nDCG@10 at the published operating point, against this
harness at the same parameters and the same analysis (harness defaults, exponential gains):

| corpus | LexiSharp BM25 (0.9, 0.4) | ParadeDB `pdb.score` (0.9, 0.4) | Δ |
|---|---:|---:|---:|
| NFCorpus | 0.305 | 0.3055 | +0.0005 |
| SciFact | 0.655 | 0.6633 | +0.0083 |

Out of the box, each engine at its own defaults:

| corpus | LexiSharp BM25 (1.5, 0.75) | ParadeDB `pdb.score` (1.2, 0.75) | Postgres `ts_rank_cd` (`simple`) | `ts_rank_cd` (`english`) |
|---|---:|---:|---:|---:|
| NFCorpus | 0.308 | 0.3084 | 0.1864 | 0.2043 |
| SciFact | 0.662 | 0.6619 | 0.0188 | not measured |
| ArguAna | 0.320 | not measured | not measured | not measured |

What each row is, and is not:

- **ParadeDB is a BM25 and reads where this library's BM25 reads.** At matched parameters the two
  differ by 0.0005 on NFCorpus and 0.0083 on SciFact, which is analysis rather than ranking: the
  index is `pdb.simple` with ASCII folding, the title is concatenated into the indexed content
  because the `pg_search` index covers `content` alone, and the matcher is a disjunction (`|||`)
  rather than this library's scored term loop. Not a reproduction, and not bit-identical.
- **`ts_rank_cd` is not BM25** — it is coverage-based and takes no k1/b, so `--backend-bm25` is
  refused with it rather than silently ignored. Most of its distance from the BM25 column is the
  *matching* rule rather than the ranking: `websearch_to_tsquery` ANDs every term, so its idea of a
  match is a document containing all of them. NFCorpus's test queries average 3.4 words and read
  0.1864; SciFact's average 13.1 and read 0.0188. PostgreSQL's own English analysis
  (`--backend-config english`) moves NFCorpus 0.1864 → 0.2043, not closer.
- **ArguAna has no row.** Both text engines refuse query text their matcher would read as a prefix
  operator, and 2 of the 1,406 test queries carry a `*` attached to a term. The lane prints that
  refusal and its count instead of a metric over the other 1,404, which would be a different query
  set from every row it sat beside.
- **The rows are not pinned.** `reference/pinned.json` keys on the analyzer and the BM25 parameters,
  and the pinned job runs without a PostgreSQL service, so a pin there could only ever skip — a gate
  that cannot fail. These numbers are reproducible from the command above, and nothing catches a
  drift in them.
- **Scores are PostgreSQL-native and not numerically comparable** to `Bm25Scorer`; the metric
  columns are. Feed any two of them to the
  [hybrid merger](../../docs/pipelines.md#hybrid-engine) when one ordering is needed.

`--backend vector --backend-ef 10,40,160` measures the ANN engine, one row per `ef_search`, over one
index — the index is the same at every value, only the candidate list the search explores changes.
The vectors are the dense lane's cached pass, so the in-memory dense row ranks the same embeddings
exactly and the distance between them is the index's cost in nDCG (NFCorpus, the one corpus this
curve was taken on):

| ef_search | nDCG@10 | MAP@10 | MRR@10 | R@10 |
|---|---:|---:|---:|---:|
| 10 | 0.2882 | 0.2077 | 0.4589 | 0.1301 |
| 40 | 0.2960 | 0.2127 | 0.4811 | 0.1388 |
| 160 | 0.3013 | 0.2151 | 0.4928 | 0.1426 |
| exact, in process | 0.304 | 0.216 | 0.502 | 0.144 |

**The index costs 0.0158 nDCG@10 at `ef_search` 10, 0.008 at 40 and 0.0027 at 160** — the recall
ratio translated into metric points. The residual at 160 is not attributed: the same cosine computed
by pgvector in single precision and in process, and on NFCorpus the planner may already answer with
an exact scan at that value (see the plan note on the backends page).

The **sparse** engine has its own lane (`--sparse`, below) rather than a `--backend` value: it takes
precomputed vectors and needs no server. The **fuzzy** engine is measured with `--backend fuzzy
--backend-threshold`, one row per `pg_trgm` similarity threshold. On the same two corpora it scores
0.0981 (NFCorpus) and 0.2186 (SciFact) at the low end of an in-sample threshold sweep, and 0.0000 /
0.0033 at the engine's default 0.3 — a threshold on trigrams is not a relevance ranking, and [the
evaluation page](../../docs/evaluation.md#the-sql-backends-on-the-same-corpora) carries the grid and
the caveats. The ANN *recall* curve (`--ann-ef`) is a separate measurement, kept because a recall
ratio and an nDCG are different questions.

## Learned sparse (optional, ONNX Runtime outside .NET)

`--sparse <dir>` scores `SparseTextSearchEngine` on vectors the harness did not compute. The library
runs no model, so a sparse row cannot encode anything: `sparse/encode_splade.py` runs a SPLADE ONNX
export once, on a machine that has the model, and writes one line per text keyed by the SHA-256 of
that text's exact UTF-8 bytes. [Its README](sparse/README.md) carries the install, the model, the
tokenizer route, the format and the encode command; nothing under `sparse/` is referenced by the
library.

The row's name — model, truncation, top-k — is read from `<dir>/<dataset>.meta.json`, written in the
same pass as the vectors, so the printed configuration cannot disagree with the file that was read.
A directory that is absent, a manifest that is missing, a malformed line, or a corpus the vectors do
not cover is refused by name (with the count and an example) instead of producing a metric over the
subset that happened to be encodable.

Measured on 2026-10-26 with SPLADE-v3 (`splade-v3.onnx`, 532 MB, sha256 `9e472022…`), 256-token
truncation, top 256 terms, documents as `title + "\n" + text`, linear gains:

| corpus | BM25 (same run, k1=1.5/b=0.75) | BM25 aligned (English, 0.9/0.4) | SPLADE-v3 | queries |
|---|---:|---:|---:|---:|
| NFCorpus | 0.307 | 0.321 | **0.3576** | 323 |
| SciFact | 0.662 | 0.679 | **0.7083** | 300 |

The sparse row's MAP@10 / MRR@10 / R@10 read 0.2646 / 0.5714 / 0.1709 on NFCorpus and 0.6678 /
0.6814 / 0.8157 on SciFact, against BM25's 0.234 / 0.523 / 0.152 and 0.633 / 0.644 / 0.806 at the
aligned operating point. What the row does and does not establish — the encode cost it excludes, the
missing ArguAna row, the pruning budget, the 8-bit export's distance from the full-precision one —
is on [the evaluation page](../../docs/evaluation.md#learned-sparse-splade-through-onnx), which is
where this repository keeps measured numbers.

## Per-query analysis

`--analyze <file>` writes one row per query per configuration, so a mean can be decomposed instead
of believed. A table that gained 0.02 nDCG does not say whether the mechanism found documents or
reshuffled ones it already had; the two call for opposite follow-ups, so the mean is not a summary
of the run but a loss of it. Each row carries the query's **lexical coverage of its own judged
documents** — the share of the query's idf mass its answer set actually says in words — the metrics,
and the deltas against `--analyze-baseline`:

| column | meaning |
|---|---|
| `judged_idf_share`, `band` | the axis the vocabulary-mismatch hypothesis is about: how much of what the query asked for its judged documents say directly. Bands cut on the idf-weighted share, **not on query length** — a three-term query can share every term with its answer and an eight-term one none. |
| `new_relevant` / `lost_relevant` | judged documents entering and leaving the top `--top-k`, per query. Only judged ids are counted, and both directions: a pure swap reads as `+1/-1`, not as a gain. |
| `delta_recall` / `delta_ndcg` | per-query metric movement against the baseline. |

`--analyze-candidate <name>` additionally prints the mean deltas per mismatch band, which is the
question the file was built for — *does the candidate help where the theory says it should, and only
there*. A band's query count is printed next to its means; a band of three must not be read as a
trend. The metrics and the new/lost counts are evaluated at the run's retrieval depth (`--top-k`).

### Retriever disagreement

`--analyze-disagreement-left <name> --analyze-disagreement-right <name>` adds four columns measuring
how far apart two configurations' top-10 rankings are, per query: `disagreement_at_10` (Jaccard
distance, `1 - |shared| / |union|`), `shared_at_10`, `left_only`, `right_only`. Both flags or
neither.

The quantity is set-based rather than rank-based on purpose: the stored ranking is truncated to the
metric depth and the two lists can have different lengths, so a Kendall or Spearman figure would be
defined by a padding convention. Two configurations that both returned nothing count as agreeing —
calling an empty result maximal disagreement would put the queries no retriever can answer at the
top of a table read as "where the expensive stage is needed".

With `--analyze-candidate` it also prints the candidate-against-baseline delta sliced by disagreement
band, then a Spearman correlation between distance and `delta_ndcg` (average ranks for ties; no
p-value, because these queries are a corpus's whole test set, not a sample). The pair it is worth
naming is a lexical row against a dense one — two answers to the same question rather than two
parameterisations of one:

```bash
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset nfcorpus --dense --no-tuned \
  --analyze /tmp/nf.tsv --analyze-baseline "BM25 (k1=1.5, b=0.75)" \
  --analyze-candidate "Hybrid BM25+Dense RRF" \
  --analyze-disagreement-left "BM25 (k1=1.5, b=0.75)" \
  --analyze-disagreement-right "Dense multilingual-e5-small"
```

Three limits worth stating before reading the table:

- **It cannot gate the dense encoder.** The distance needs both rankings, so it is measurable only
  after both retrievers have run. It can inform fusion weighting or a reranking trigger; it cannot
  decide whether to pay for a retriever in the first place.
- **The depth is the captured depth.** The per-query ranking is cut at `MetricDepth.Cutoff` (10), so
  the columns are a property of the top-10. Asking for depth 100 means capturing deeper, which costs
  the memory `--analyze` already warns about on ArguAna.
- **Band cut points (0.2 / 0.5 / 0.8) are a reading, not a result.** The summary prints the count of
  queries with identical top-10 sets and the count with none in common, because a distribution that
  is 1 almost everywhere bands the corpus into one row and says nothing about separation.

## Query-side expansion

`--expand-density <shares>` adds one BM25 row per window-density cut, where each row wraps the
scorer in the library's `ExpandingTextSearchEngine` and learns its `PmiTermExpander` from this
run's corpus text — no reindexing, and the BM25 statistics are the same ones the lexical rows read,
which is the point: `ExpansionTextIndex` is the document-side variant and changes `avgdl`, so a
comparison against it would be a comparison of two indexes. The share is the fraction of the
corpus's sliding windows a term may appear in and still be a neighbour:

```bash
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset nfcorpus --no-tuned \
  --expand-density 0.1,0.3 --expand-ranking both --analyze /tmp/expand.tsv \
  --analyze-candidate "BM25 + query expansion (density<=0.1, PositiveMutualInformation)"
```

`--expand-ranking` orders each input term's candidate neighbours before the budget is applied:
`count` (raw co-occurrence count, the classic expansion bias) or `pmi` (positive mutual
information, which declines to promote a common term that merely appears nearby); `both` adds one
row per statistic. Expansion terms carry a weight in `(0, 1]`, but `ExpandingTextSearchEngine`
ignores it — every term, original and expanded, scores as one occurrence — so these rows measure
the reach of the neighbourhood at equal weight, and `--query-term-frequency` changes that by
scoring a repeated term once per occurrence. The empty list is the default; the learning pass is
the genuinely expensive step in a run (seconds and hundreds of megabytes of pair counts on
SciFact), which is why nothing is learned unless a cut is asked for.

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
query multiplies its weight; this library deduplicates by default, which is the natural reading of a
bag of words. Where queries are short the two are identical to four decimals (NFCorpus 0.3215 either
way, SciFact 0.6788 either way). Where a query *is* a document they are not: ArguAna's every test
query is a whole ~200-word argument whose content words repeat, and counting them takes nDCG@10
from **0.240 to 0.301** at k1=0.9/b=0.4, a **+0.061** effect. Reproduce it with
`--analyzer english --reference-bm25 0.9,0.4 --query-term-frequency`, and the default on the same
line without the flag. The linear gain convention returns the same 0.301, so this is not an artifact
of the metric convention.

**`--query-term-frequency` reaches the scorer on one path only, so a figure that depends on it
being effective is a figure about a code path, not about BM25.** The flag feeds exactly one thing —
the `QueryTermWeighting` argument of `new Bm25Scorer(...)` — and that scorer goes into
`RankedTextSearchEngine`, which deduplicated the query before the scorer could see a repetition. Any
claim of the form "this setting is worth X" measured through an engine that deduplicates first is
measuring nothing, and it will report a positive number while doing so. Measured through the scorer
directly, the setting is worth **+0.061** on ArguAna at matched parameters.

**`--exclude-query-doc` drops the query's own document, which the published figure was produced with.**
1298 of the 1406 ArguAna test queries have a query id that is a document
id, and their own document is a near-duplicate that ranks first, so dropping it matters. Measured at
the pin's configuration (English analysis, k1=1.5/b=0.75 linear): **0.313 without, 0.4180 with, i.e.
+0.105.** The pin `arguana/english+exclude` carries the value with the flag on; the measurement
without it is the plain row above. An earlier estimate of +0.0012 for this flag was the option doing
nothing — the exclusion was not applied on the term-at-a-time path — and it was wrong; the fix is what
made the flag reach its document (see `reference/pinned.json`).


`--fingerprint` reports what the index holds — documents, non-empty documents, total terms — next to
the counts an independent implementation's index of the same corpus holds. Those are integers, and
they say something a score cannot: whether the two runs indexed the same thing. Measured, with the
reference analysis: NFCorpus 655,155 terms against 637,485 (2.8%), SciFact 850,694 against 838,128
(1.5%), ArguAna 980,418 against 969,528 (1.1%). With the default analyzer the same corpora read
838,401 / 1,111,245 / 1,407,061 — 31% to 45% larger, which is most of the apparent gap in the tables
below.

## The regression net

`--verify-reference` replays every configuration in [`reference/pinned.json`](reference/pinned.json)
and exits 1 on any drift. It is the `Pinned reference` workflow, on a dispatch, on a push that
touches the library or this harness, and on a weekly schedule, so a drift is a red check rather
than a wrong README. The file holds two kinds of number, and they are not interchangeable:

- **Regression pins** — the value this repository must keep producing, at a named configuration
  (analyzer, gain convention, BM25 parameters, query-syntax state, query-document exclusion,
  query count). The
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

### Two rules for writing a pin

**A pin is a norm, not a by-product.** It states what a configuration produces, independently of
whatever the metrics table happens to do today. `Measure` therefore builds its own tokenizer, query
list and options rather than calling into the table, and a change to the table's defaults must not
move a pin: if it did, the gate would be asserting the table against itself and would stay green
through exactly the regression it exists to catch.

**A pin must name everything that changes the number.** Anything it leaves unnamed is a setting it
inherits, and an inherited setting is invisible to it — change that default and every pin moves
together while the gate reports success. The test is not "which `SearchOptions` fields are there",
and it is not an enumeration: an enumeration is a list of what somebody remembered this time, and the
next hole will be whatever the list did not mention. The test is *trace every value the number is
computed from, and name each one.* That reaches past search options into how the index was built —
the average-length divisor and the document-length quantization, which on ArguAna move every score
by 6e-03, a hundred times the residual they exist to protect, and which no search-time option can
express at all. A value that would have to be guessed in order to reproduce the figure belongs in the
file, whether it is a token, a number, or a property of the index.

The corollary: a configuration that cannot be written down is not a pin that can be added later. The
schema today cannot express the analyzer whose separator rules reach the published ArguAna figure,
and it cannot express the index statistics that figure needs. Both are holes in the type, not
policies about what is worth pinning.

The distinction that keeps those two from colliding, because it is not obvious from either:
**sharing a definition is safe when the pin names it, and inheriting one is not.** The tokenizer a
pin uses is a definition, and it may be built in one place — a pin that says `"analyzer": "uax29"`
and gets the wrong `uax29` is a real defect, and two copies of that table drifting apart is how it
happens. A default the pin does not mention is not a definition it shares, it is a value it is
taking on trust, and no amount of sharing fixes it. So: one construction per definition, referenced
by name; every setting a number depends on, written down.

**A shared definition carries a version, and a pin records the one it was recorded against.**
Building the analyzer table in one place removes the second copy that could drift; it introduces a
different hazard, which is that a *change* to that table silently changes what every pin's name
means. `"analyzer": "uax29"` recorded against one table and replayed against another produces the
same word and a different analysis, and no delta appears until the run drifts. So a definition that
pins reference by name is versioned — the index cache here already stamps `version=2` and the dense
cache `CacheVersion = 3`, for exactly this reason — and a pin stores the version it was recorded
under, and says so when it no longer matches. An edit to a shared definition is then reviewed as
what it is, a change of semantics, rather than landing as a refactor.

This is the same failure as an unnamed default, one level up: there, a value the pin did not name
changed under it; here, a value the pin *did* name points somewhere else. Naming a thing is not the
same as pinning it, and the difference is only a version field.

Where a rule can be made structural, make it structural rather than written: a configuration whose
fields are all required cannot forget to name one. The rule exists to say why that is wanted; the
type is what makes it hold.

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
`k1 × b × δ` product (125 points) on the same queries, on nDCG, as `BM25 tuned`. Both sides must be
fitted the same way for this to compare formulas: a variant at a fixed paper `δ` against a BM25
whose `(k1, b)` were fitted is not a comparison of anything.

| Corpus | `BM25 tuned` | `BM25+ tuned` | `BM25L tuned` | unfloored baseline | `DeltaHelped` |
|---|---|---|---|---|---|
| NFCorpus | 0.311 (k1=2, b=0.5) | 0.311 (δ=0) | 0.311 (δ=0) | 0.311 | false, both |
| SciFact | 0.664 (k1=1.5, b=1) | 0.666 (δ=0.25) | 0.668 (δ=0.25) | **0.664** | true, both |
| ArguAna | 0.363 (k1=2, b=1) | 0.363 (δ=0) | 0.363 (δ=0) | 0.328 † | false, both |

On NFCorpus the three tuners independently select identical parameters and identical scores, so
those three rows are one result reported three times: the δ search picks no bound and hands back
BM25. On SciFact δ = 0.25 wins, and the **unfloored baseline the harness prints is 0.664 — exactly
`BM25 tuned`'s score**, which confirms at corpus scale that both variants reduce to BM25 at δ = 0.
That makes the +0.002 / +0.004 attributable to δ rather than to a wider grid, since both searches
cover the same 25 `(k1, b)` pairs. It is still 300 queries, binary relevance, best-of-125, fitted
in-sample on the queries it is scored on: an upper bound, not a result. ArguAna's row was fitted
afterwards and is in the table above: both variants pick δ = 0 and read the tuned BM25's 0.363, so
the omitted bound is what closes the untuned 0.051/0.044 gap.

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
term doing something useful.

**BM25F tuned against BM25 tuned, on the three corpora** (`--tune-bm25f`, 29 configurations each,
fitted in-sample on the queries they are scored on — oracles, not fair baselines):

| Corpus | `BM25 tuned` | `BM25F tuned` | fitted `(k1, b, title)` | `WeightingHelped` |
|---|---:|---:|---|---|
| NFCorpus | 0.311 | 0.309 | 2, 0.25, 1.0 | false |
| SciFact | 0.664 | 0.668 | 1, 0.5, 1.0 | false |
| ArguAna | 0.363 | **0.397** | 1.5, 1, 1.0 | false |
| reference corpus | 0.8978 (nDCG@5) | 0.8867 | — | false |

† ArguAna's unfloored baseline prints 0.328 against the row's 0.363: the tuner scores candidates
with query syntax on and the table reports with it off, which costs 0.031 on this corpus. Every
tuned row in this file is chosen under the tuner's convention; the ArguAna section documents it.

So the comparison has a sign that depends on the corpus — BM25F ahead by 0.034 on ArguAna, 0.004 on
SciFact, behind by 0.002 on NFCorpus and 0.011 on the reference corpus — and **the field weighting
is never what moves it**: every fitted title weight is 1.0, i.e. neutral. What ArguAna's +0.034
buys is BM25F's single-field length term at (k1=1.5, b=1), not a title boost. That is the same
conclusion the reference corpus reached, now measured three more times.

Those two figures were **equal at 0.8812** in an earlier version of this file. The equality was an
artifact of the objective, not a property: the run tuned on F1@5, which is flat on the reference
corpus (0.3588 for every configuration), so neither tuner had any signal to fit and both took the
first grid point their tie-break reached. Re-fitted on nDCG@5 — the metric they are reported in —
BM25F tuned *loses* to BM25 tuned by 0.011. The conclusion for the reference corpus is unchanged
and better supported.

Which leaves the ArguAna 0.379 as an **untuned** claim that has now been tested, and it survives:
`--tune-bm25f` fits `(k1=1.5, b=1)`, keeps the title at **1.0**, and reads **0.397** — against
BM25 tuned's 0.363. So on this corpus BM25F tuned *does* beat a tuned BM25, by 0.034, and
`WeightingHelped = False` says the margin is not the field weight: it is what a single-field BM25F
scores at those parameters. One corpus where it leads, one (the reference corpus) where it trails,
both in-sample. The tuner's own score for that configuration is 0.362 against the row's 0.397,
which is the query-syntax convention documented under [the tuned rows](#results-arguana-k10-1406-test-queries),
not a discrepancy. And the number is **not** the 0.3970 the reference publishes for this corpus:
that one is BM25 at k1=0.9/b=0.4 under the reference analysis, this one is BM25F at k1=1.5/b=1 under
the library defaults. The three matching decimals are a coincidence, not a reproduction.

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
| Dense multilingual-e5-small        |   0.655 |  0.606 |  0.619 | 0.793 |
| Hybrid BM25+Dense (weighted)       |   0.681 |  0.634 |  0.645 | 0.812 |
| Hybrid BM25+Dense (RRF)            |   0.686 |  0.637 |  0.651 | 0.821 |
| BM25 (top100) → cross-encoder      |   0.653 |  0.606 |  0.620 | 0.778 |
| QL (top100) → cross-encoder        |   0.649 |  0.604 |  0.618 | 0.770 |
| Hybrid RRF (top100) → cross-encoder |  0.664 |  0.614 |  0.626 | 0.804 |

BM25 at 0.662 vs the 0.665 BEIR reference — within 0.5 % on a dataset whose claims use exact
terminology, so the stemming gap (large on NFCorpus, see [Stemming](#stemming)) nearly disappears
here; the tuned k1=1.5/b=1 operating point reproduces the reference at 0.664.

SciFact is the friendliest case for a title weight in this whole harness — BM25F at title 2.0
reaches 0.665, level with the published BM25. It is still +0.003 over BM25's 0.662 on 300 queries,
which is not a result; treat it as "weighting did not hurt here", not as "weighting helps".

**The dense rows exist here now, and the cross-encoder loses on this corpus.** The e5-small
retriever alone reads 0.655, just below BM25's 0.662; fusing the two reaches 0.681 (weighted) and
**0.686** (RRF), the best non-reranked row here. Stacking the cross-encoder on that fusion **costs
0.022** (0.664), on BM25 it costs 0.009 (0.653), and only on query likelihood does it help (+0.027,
0.649). On NFCorpus the same reranker improved every first stage; here it improves one of three, so
"the cross-encoder helps" is a corpus-dependent statement and this is the corpus that says so. The
cost is visible in the run: 1,718–1,890 s for 300 queries, against 0.1 s for the lexical pass.

The pgvector index over the same embeddings costs what its recall says it should: 0.5998 at
`ef_search` 10, 0.6528 at 40 and 0.6550 at 160, against the exact in-process 0.655. That is
**−0.055 / −0.002 / −0.000 nDCG@10** — the same shape as NFCorpus, where the low end of the curve is
much more expensive.

## Results (ArguAna, k=10, 1406 test queries)

Default tokenizer, **no stemming** — see [Stemming](#stemming) for the same run with
`--stem porter`.

| Config                             | nDCG@10 | MAP@10 | MRR@10 |  R@10 |
|------------------------------------|--------:|-------:|-------:|------:|
| BM25 (k1=1.5, b=0.75)              |   0.320 |  0.207 |  0.207 | 0.679 |
| BM25 (k1=1.2, b=0.75)              |   0.313 |  0.203 |  0.203 | 0.663 |
| TF-IDF                             |   0.002 |  0.001 |  0.001 | 0.004 |
| QueryLikelihood (λ=0.2)            |   0.302 |  0.198 |  0.198 | 0.632 |
| Hybrid BM25+QL (weighted)          |   0.320 |  0.207 |  0.207 | 0.679 |
| Hybrid BM25+QL (RRF)               |   0.317 |  0.206 |  0.206 | 0.668 |
| BM25F (unweighted)                 |   0.379 |  0.254 |  0.254 | 0.773 |
| BM25F (title 2.0)                  |   0.379 |  0.253 |  0.253 | 0.775 |
| BM25F (title 4.0)                  |   0.375 |  0.249 |  0.249 | 0.772 |
| BM25F tuned (k1=1.5, b=1, title=1) |   0.397 |  0.269 |  0.269 | 0.797 |
| BM25+ (delta=1.0)                  |   0.269 |  0.172 |  0.172 | 0.578 |
| BM25L (delta=0.5)                  |   0.276 |  0.178 |  0.178 | 0.587 |
| BM25 tuned (k1=2, b=1)             |   0.363 |  0.240 |  0.240 | 0.751 |
| BM25+ tuned (k1=2, b=1, δ=0)       |   0.363 |  0.240 |  0.240 | 0.751 |
| BM25L tuned (k1=2, b=1, δ=0)       |   0.363 |  0.240 |  0.240 | 0.751 |
| Dense multilingual-e5-small        |   0.313 |  0.208 |  0.208 | 0.647 |
| Hybrid BM25+Dense (weighted)       |   0.349 |  0.231 |  0.231 | 0.725 |
| Hybrid BM25+Dense (RRF)            |   0.350 |  0.229 |  0.229 | 0.731 |
| BM25 (top100) → cross-encoder      |   0.302 |  0.203 |  0.203 | 0.615 |
| QL (top100) → cross-encoder        |   0.301 |  0.203 |  0.203 | 0.609 |
| Hybrid RRF (top100) → cross-encoder |  0.339 |  0.229 |  0.229 | 0.688 |

**The BM25+ / BM25L rows here are the worst the variants do anywhere — and the tuned rows answer
the question the untuned ones left open.** At a fixed paper δ they lose 0.051 and 0.044 against
BM25, roughly 15 % relative. The 125-point grid has now been run on this corpus, and **both variant
tuners choose δ = 0**, so their tuned rows are the tuned BM25's own 0.363, at the same k1=2, b=1:
tuning closes the gap by removing the bound rather than by finding a better one. That is a negative
result for the lower bound, consistent with NFCorpus and with SciFact's +0.002/+0.004, and it means
the untuned 0.269/0.276 are what a fixed δ costs on this corpus, not what the formula costs when
fitted.

Two caveats on the tuned rows, both from the run itself. The winner sits at the **corner of the
grid** (k1=2 is the largest k1 offered, b=1 the largest b), so the optimum may lie outside it —
`Bm25ParameterTuner` does not expand its grid. And the tuners score their candidates with
`SearchOptions` defaults, where `ParseQuerySyntax` is **on**, while the harness scores the chosen
row with it **off**: on this corpus 148 of the 1,406 test queries are phrases under that detector,
which the tuner reads as adjacency and the table reads as text. That convention is not a rounding
detail here: running the whole harness with `--query-syntax` takes BM25 at the defaults from 0.320
to **0.289**, a 0.031 drop, and that is the size of the disagreement above (0.363 against the
tuner's 0.328 for the same δ=0 point). The tuned row is therefore a parameter choice made under one
convention and reported under another; on NFCorpus and SciFact the two are identical, because no
query in either carries syntax. The four tuners take an optional `SearchOptions` template, and
`--tune-search-options` passes the run's own options to all four, so a run can fit its candidates
under the convention it reports with. The row above predates that switch and has not been re-fitted
through it, which is why the flag is off by default: turning it on changes a published parameter
choice without re-measuring it.

**Fusion beats both parents here too, and it is the second corpus where that holds.** Dense alone
reads 0.313, just below BM25's 0.320; BM25+dense reaches **0.349** (weighted) and **0.350** (RRF),
so the +0.029/+0.030 over BM25 is the same shape as NFCorpus and SciFact.

**The cross-encoder loses to every first stage it is stacked on here**, which makes this the corpus
where the second stage is uniformly negative: BM25 0.320 → 0.302, QL 0.302 → 0.301, RRF fusion
0.350 → **0.339**. SciFact had it help QL and hurt the other two; NFCorpus had it help all three.
So the honest summary is that the reranker pays on short-query corpora with a few judged documents
and does not pay on long-query ones — and this is the extreme long-query corpus, 196 words per
query against NFCorpus's 3.4.

The three rows cost 8,057 s, 3,625 s and 3,342 s of wall clock in the run's own elapsed column,
and the cache is why the second and third were affordable: **175,420 of 394,621 pair-requests were
served from the first row's passes** (44 %). An `ef_search` curve for this corpus is still absent,
for a different reason — the vector engine refuses 148 of the 1,406 test queries as phrases, so the
curve would be over a reduced query set.

**The largest BM25F margin anywhere in this harness, and now with its tuned row.** 0.379 against
BM25's 0.320 on 1406 queries, both under the library defaults, and **0.397** once `(k1, b)` are
fitted — the tuner chose `k1=1.5, b=1` and kept the title weight at **1.0**, so `WeightingHelped`
is `False` and the weighting is not the cause. The three fixed rows say the same: unweighted,
title 2.0 and title 4.0 are within 0.004 of each other, and the heaviest weight is the worst. What
the fitted row does establish is that **this corpus is one where BM25F tuned beats BM25 tuned**
(0.397 against 0.363); the reference corpus is where it trails, by 0.011. Two corpora, opposite
signs, both in-sample.

ArguAna is also the corpus least like ordinary search — counter-argument retrieval, very long
queries, a `title` that is a topic phrase rather than a headline. One more caveat on reading that
0.379: **each ArguAna test query has exactly one relevant document** (the counter-argument), so
Recall is quantized and nDCG is far easier to move than on a corpus with a dozen relevant documents
per query. A scorer that gets the one right document higher gains a lot; one that reshuffles the
bottom gains nothing.

BM25 over the full 1406-query test split lands at **0.320 under the library defaults**. Two things
follow from that, and both are about the comparison rather than the scorer. The 0.315 it invites
comparison against is the 2021 paper's figure, while the current reference for this index is
**0.3970** — so a default-configured run is not 8 % below anything, it is a different configuration.
And the gap between the two configurations is not a parameter: it is four analysis rules, one
document count, and a metric cutoff.

| configuration | nDCG@10 |
|---|---:|
| library defaults (the table above) | 0.320 |
| + reference analysis, at k1=0.9/b=0.4 | 0.240 |
| + reference analysis, + query-frequency counting, at k1=0.9/b=0.4 | 0.301 |
| + reference analysis, + query-frequency counting, at k1=3.0/b=0.75 | 0.367 |
| + the parity options, at k1=0.9/b=0.4 | 0.3970 |
| published reference, at k1=0.9/b=0.4 | 0.3970 |

Read the third and fourth rows together: the setting is worth +0.061 at the reference's own
parameters and +0.033 at k1=3.0. Those are settings the library exposes; the fifth row is the one
that reaches the published figure, and it is worth being exact about what it is. It adds the four
options a bit-for-bit comparison needs — the document count that carries the field rather than every
indexed document, the reference's scale and precision, its four-decimal rounding, and the query's own
document removed — all off by default. What they buy is the arithmetic, not the ranking: with them
the 15,466 returned scores for the 1,406 queries match the reference's own searcher on the raw bits,
and without them the same ranking is reached by a different route and scores the same order.

The sixth row is measured outside this harness. `trec_eval` on a run this harness writes reads
nDCG@10 0.3970 and recall@100 0.9324, matching both published figures, so the claim does not rest on
the metric code above. A residual of 0.0004 against `trec_eval` remains, confined to the 102 of 1,406
queries whose returned scores contain a tie; the mechanism is not established and the sweep records
it as measured rather than explained.

An earlier figure published for this corpus was withdrawn; it is not reproducible by any code path in
this repository. See the [changelog](https://github.com/manuc66/LexiSharp/blob/main/CHANGELOG.md). The
like-for-like comparison at the reference's own parameters is the fifth row. A parameter grid is not what
separates the two configurations: the difference is four analysis rules, one document count, and a
metric cutoff.

The table above is the full 6-config, full-split run. Reproduce it with
`dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset arguana --no-tuned`. Note that
`--limit` evaluates only the first N test queries and reports a figure over those, so a partial run
is not comparable with a full-split one.

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
| BM25 (k1=1.5, b=0.75)     |    0.308 | **0.322** |   0.662 | **0.687** |   0.320 | 0.308 |
| BM25 (k1=1.2, b=0.75)     |    0.306 | **0.321** |   0.660 | **0.687** |   0.313 | 0.300 |
| TF-IDF                    |    0.248 | **0.257** |   0.345 | **0.362** |   0.002 | 0.003 |
| QueryLikelihood (λ=0.2)   |    0.288 | **0.298** |   0.622 | **0.648** |   0.302 | 0.312 |
| Hybrid BM25+QL (weighted)  |    0.308 | **0.322** |   0.662 | **0.687** |   0.320 | 0.308 |
| Hybrid BM25+QL (RRF)       |    0.300 | **0.310** |   0.644 | **0.669** |   0.317 | 0.315 |
| Published reference       |    0.325 |    —    |   0.665 |    —    |   0.315 |   —   |

This table switches the **stemmer only**, which is what makes it readable and also what bounds it: the
analysis a published measurement was produced with is a stemmer *and* a stop word list, and only the
first half is isolated here.
`--analyzer english` turns on both, and reaches 0.327 on NFCorpus and 0.692 on SciFact — above the
published figures, where this table's best is 0.322 and 0.687. The stemmer's own contribution is
therefore worth about +0.014 on NFCorpus and the stop words another +0.005; on ArguAna both cost
something, and the gap there is not analysis at all — see `--query-term-frequency` above.

**Stemming helps on two corpora and is mixed on the third.** On NFCorpus every config improves and
BM25 goes 0.308 → 0.322, taking the gap to BEIR's 0.325 from −5.2 % to −0.9 %; on SciFact
0.662 → 0.687 puts it *above* the 0.665 reference (R@10 0.781 → 0.818). On ArguAna the effect is
smaller and direction-dependent — BM25 0.320 → 0.308, QL 0.302 → 0.312 — so stemming neither
explains that corpus's gap nor widens it by much. MAP@10, MRR@10 and R@10 move with nDCG@10 in
all three columns.

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
- `--heldout <share>` fits every tuner on a hash-chosen part of the validation queries and puts each
  tuned row's fit-set figure beside its score-set figure, so the oracle becomes a measured pair
  instead of a caveat. The split is FNV-1a 64 of the query id modulo 1,000,000, so the command
  reproduces it; the measured pairs and the remaining upper bounds are in
  [docs/evaluation.md](../../docs/evaluation.md#held-out-measurement---heldout).

## Decisions worth knowing

- Not in the `ci.yml` test suite, and that is a reason of resource and of the wrong unit: it
  downloads data over the network, so a unit test that pulls ~100 MB of corpora is not a unit test,
  and a build that cannot run without a download is a build that fails for reasons unrelated to the
  commit. It runs instead as the `Pinned reference` workflow, on a dispatch, on a push that
  touches `src/` or this harness, and weekly, and it is not a *required* check - making it one is a
  repository setting, not a file in here. The `LexiSharp.Benchmarks` project (BenchmarkDotNet) stays
  a *performance* harness; this one is a *quality* harness.
- The dense path is opt-in (`--dense`) because the first run downloads ~470 MB and encodes on
  CPU for several minutes; it is resource-bounded (4 threads) and cached, so it never
  re-encodes. The reranker is opt-in (`--rerank`) for the opposite reason: it is cheap to
  download (~91 MB) but the ranking itself is compute-heavy and dominates run time.
- Since it only depends on the core library, the BCL and ONNX Runtime (eval project only), no
  dataset or model code is shipped in the core package.