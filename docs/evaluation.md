---
title: Evaluation
permalink: pretty
nav_order: 11
---

# Evaluation

A ranking you cannot score is a ranking you are guessing about. This page is the evidence
first, then the instruments: standard IR metrics, a parameter tuner, a benchmark CLI over
your own corpus, and a per-query diff that says *which* queries a change helped.

Everything quoted here is traceable to a command you can re-run. Where something is **not**
measured, it says so rather than being quietly left out.

## Measured against published baselines

`bench/LexiSharp.Eval` runs the engines over three public BEIR corpora — NFCorpus, SciFact
and ArguAna — md5-verified on download, and reports nDCG@10 / MAP@10 / MRR@10 / R@10 next
to the BM25 numbers published in the BEIR paper (Thakur et al. 2021, Table 2). Reproduce
it with `dotnet run --project bench/LexiSharp.Eval`; the full per-dataset tables are in
[its README](https://github.com/manuc66/LexiSharp/blob/main/bench/LexiSharp.Eval/README.md),
and it grants no licence over the datasets it downloads.

**The plain BM25 baseline, against the published reference (nDCG@10):**

| Corpus | LexiSharp BM25 | BEIR's BM25 | Δ |
|---|---:|---:|---:|
| NFCorpus (323 judged queries) | 0.308 | 0.325 | −5.2 % |
| SciFact (300 judged queries) | 0.662 | 0.665 | −0.5 % |
| ArguAna (1406 judged queries) | 0.289 | 0.315 | −8.3 % |

That is the honest headline: a dependency-free BM25 lands within half a point of a
reference implementation on SciFact and 5–8 % below it on the other two. A gap that size is
a starting point worth measuring, not a claim of parity.

**NFCorpus, the one corpus where the whole stack is measured** (nDCG@10 / MAP@10 / MRR@10 /
R@10, 323 judged queries, default tokenizer, no stemming):

| Config | nDCG@10 | MAP@10 | MRR@10 | R@10 |
|---|---:|---:|---:|---:|
| BM25 (k1=1.5, b=0.75) | 0.308 | 0.222 | 0.516 | 0.146 |
| BM25 (tuned, in-sample) | 0.311 | — | — | — |
| TF-IDF | 0.248 | 0.171 | 0.399 | 0.128 |
| Query likelihood (λ=0.2) | 0.288 | 0.205 | 0.485 | 0.132 |
| BM25F (unweighted / title 2.0) | 0.296 | 0.211 | 0.484 | 0.141 |
| Dense (multilingual-e5-small) | 0.304 | 0.216 | 0.502 | 0.144 |
| Hybrid BM25+dense (weighted) | 0.323 | 0.230 | 0.534 | 0.156 |
| Hybrid BM25+dense (RRF) | 0.333 | 0.239 | 0.550 | 0.160 |
| BM25 → cross-encoder rerank | 0.337 | 0.246 | 0.558 | 0.153 |
| **Hybrid RRF → cross-encoder rerank** | **0.346** | **0.250** | **0.573** | 0.158 |
| BEIR's published BM25 | 0.325 | — | — | — |

What that table says:

- the **dense retriever alone** sits at BM25 level (0.304 vs 0.308) on a model it does not
  ship — the harness supplies `multilingual-e5-small` over ONNX;
- **fusing beats both parents** (0.323–0.333 against 0.308 and 0.304), and the
  **cross-encoder improves every first stage** it is stacked on: **0.346** for BM25+dense RRF
  plus reranking, about +2.1 points over BEIR's published BM25;
- **BM25F sits below BM25 here and the title weight moves nothing** — a negative result, and
  the reason is default parameters rather than the technique: tuned against tuned, the two
  are identical on the reference corpus. See
  [BM25F](ranking.md#bm25f-field-weighted-bm25) for that correction in full.

What it does **not** say:

- the dense and cross-encoder rows are the only ones that use a real model, and both belong
  to the harness, not to the library. SciFact and ArguAna have no dense or cross-encoder
  rows at all, so the full stack is measured on **one** of the three corpora, not three;
- stemming is not settled either: `--stem porter` takes NFCorpus 0.308 → 0.322 and SciFact
  0.662 → 0.687, and *hurts* ArguAna 0.289 → 0.279. That is why it stays opt-in — see
  [Tokenizer customization](indexing.md#tokenizer-customization);
- ArguAna's BM25F 0.344 is **untested, not a result**: it is default-vs-default, and a tuned
  BM25 over its 1406 queries did not fit the compute available here.

## Metrics

`RetrievalMetrics` covers the standard set, so a number means here what it means in the
literature:

| Metric | The question it answers |
|---|---|
| `Precision@k`, `Recall@k`, `F1@k` | how much of the page was right, how much of the truth was found |
| `nDCG@k`, binary and **graded** (exponential gains) | is the right document near the top, weighted by its grade |
| `ReciprocalRank@k` (→ MRR) | how high the first relevant document appears |
| `AveragePrecision@k` (→ MAP) | precision at every rank where a relevant document appears |

Relevance grades come from the qrels file. A query carrying no judgement is loaded and
counted in the run, but excluded from the averages rather than scored as a zero — silently
averaging in a zero is how a harness flatters itself.

## Tuning BM25 and BM25F

Let the corpus pick its own parameters. `Bm25ParameterTuner` grid-searches `k1` and `b`
against your own validation queries, judged by `Precision@k`, `Recall@k`, `F1@k` or
`nDCG@k`; `Bm25FParameterTuner` does the same for BM25F's `k1`/`b` and its per-field
weights, and adds one question of its own.

**A tuned score is an oracle, not a fair baseline.** The winner was fitted on the same
queries it is scored on, and the more configurations were tried, the more of the gain is
fitting noise. Score it on a held-out set, or treat it as an upper bound. Every comparison
in this documentation that claims a scorer "beats" another was run tuned-against-tuned for
exactly this reason — a default-vs-default comparison mostly measures which default fits
the corpus.


`Bm25FParameterTuner` grid-searches `(k1, b)` and the weight of each named field, in two stages:
`(k1, b)` first with fields neutral, then the weights at that winner. That is coordinate descent,
not an exhaustive product, and the class says so — a configuration that is only good jointly can be
missed. The run is refused above a configuration cap, with the count, rather than silently trimmed.

```csharp
var result = new Bm25FParameterTuner(index, validationQueries).Tune(
    weightedFields: ["title"],
    weightValues: [1.0, 1.5, 2.0, 3.0],
    topK: 10,
    metric: TuningMetric.Ndcg);

if (result.WeightingHelped)
    Console.WriteLine($"title weighs {result.Parameters.FieldWeights["title"]}");
else
    Console.WriteLine($"no weighting beat neutral; best was k1={result.Parameters.K1} b={result.Parameters.B}");

var engine = new RankedTextSearchEngine(index, new Bm25FScorer(result.Parameters));
```

Two things it deliberately reports rather than hides:

- **`WeightingHelped` is the answer to "does weighting help on my corpus".** It is `false` on the
  reference corpus and on all three BEIR corpora tried. `1.0` belongs in `weightValues` for exactly
  this reason: without a neutral candidate the search cannot conclude that nothing helps.
- **The best score is an oracle, not a fair baseline.** The winner was fitted on the same queries it
  is scored on, and the more configurations were tried the more of the gain is fitting noise. Score
  the result on a held-out set or treat it as an upper bound. `Bm25Tuned` has carried this caveat
  all along; this one does too.

Reuse the same `Bm25ValidationQuery` input as `Bm25ParameterTuner` — a single validation type for
both, deliberately.

**Without a field-aware index it refuses, by name.** Scoring against an index whose
`HasFieldStatistics` is `false` throws `NotSupportedException` naming the index rather than ranking
on zeros:

```csharp
// Throws: "BM25F needs per-field statistics, and FlatIndex has none…"
new Bm25FScorer().Score("1", ["ranking"], someIndexWithoutFields);
```

## Benchmark CLI (`LexiSharp.Cli`)

Compare ranking strategies over your own corpus without writing code. The runner
(`LexiSharp.Benchmarking` in the core) builds one shared in-memory index, evaluates every
selected configuration against the same labeled queries and reports nDCG/MAP/MRR/Recall/
Precision/F1 at the retrieval depth plus the per-query latency:

```
dotnet run --project bench/LexiSharp.Cli -c Release -- benchmark ./notes \
    --queries queries.json --qrels qrels.tsv --top-k 10
```

- `--queries` accepts a JSON object `{ "id": "query text", ... }` or a TSV `id⇥text`.
- `--qrels` is a TSV `qid⇥docid[⇥grade]` (grades are read as binary relevance); a query
  without any judgment is loaded but excluded from the metric averages.
- `--configs` selects the comparison: `bm25`, `bm25-tuned` (fits `(k1, b)` on the labeled
  queries), `tfidf`, `ql` (query likelihood), `hybrid` (RRF over BM25 + TF-IDF).
- `--json <path>` writes the results as a machine-readable report.

The same comparison is available in-process through `CorpusBenchmark.Run` over any
`IReadOnlyCollection<SearchDocument>` and `BenchmarkQuery` set, with custom engines reachable
through the public `BenchmarkConfig` constructor.

## Comparing two configurations, query by query

A pair of means cannot tell you what a change actually did. Two configurations can land
0.028 apart while one of them rescued a query and lost two others — and the mean is silent
about both.

```bash
dotnet run --project bench/LexiSharp.Cli -c Release -- diff bench/reference-corpus/corpus \
    --queries bench/reference-corpus/queries.json --qrels bench/reference-corpus/qrels.tsv \
    --baseline bm25 --candidate bm25-semantic --top-k 5
```

Recorded output of that exact command, corpus-derived expansion against plain BM25 — a
negative result, and the reason the lane is not a default:

```
mean nDCG@5: 0,8751 -> 0,8469 (-0,0282 per query)
queries: 0 improved, 2 degraded, 20 unchanged (net -2)

DEGRADED by BM25 + semantic
  long-document-02         0,500 -> 0,000 (-0,500)  lost: the judged document at rank 3 is gone
    "how often should a token be exchanged"
  paraphrase-02            0,579 -> 0,459 (-0,120)  demoted: rank 2 -> 3
    "how long is an access token valid"

Read: the candidate loses more queries than it gains (2 vs 0). A mean can improve while this is true.
```

The `Reading` on each line names the cause rather than restating the numbers, because *lost*,
*rescued*, *demoted* and *same rank* are four different bugs. A net of zero is not "no change": it
is movements that cancelled out, which is precisely what a mean hides.

`CorpusBenchmark.Run` returns the same breakdown in `BenchmarkConfigResult.PerQuery` (metrics per
query, the retrieved ids, and the 1-based rank of the first judged document), and
`BenchmarkComparer.Compare(baseline, candidate, epsilon)` builds the comparison. It refuses runs over
different query sets or in different orders rather than reporting a comparison against nothing.
`--epsilon` (default `1e-9`) is the score difference below which a query counts as unchanged; the
reported mean delta stays raw arithmetic.

The [reference corpus](https://github.com/manuc66/LexiSharp/blob/main/bench/reference-corpus/README.md) is the corpus the command above was run
against.

## Performance

Latency and allocation numbers live in [Benchmarks](benchmarks.md): BenchmarkDotNet runs on
one named machine, with its configuration and the reproduce command. Read them as
indicative and re-run the suite on your own hardware. Of the two kinds of number there, the
one that travels is **allocation** (counted in bytes, host-independent); wall-clock means
from a shared or contended machine are not publishable, and none are quoted from CI.
What *does* gate a build is deterministic and clock-free: `lexisharp verify`, which replays
the reference corpus against a committed baseline.
