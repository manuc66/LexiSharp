---
title: Retrieval quality and IR metrics
nav_order: 11
description: >-
  Retrieval quality on the BEIR corpora, the metrics behind it (nDCG, MRR, MAP,
  recall), the benchmark CLI, per-query diffs and BM25 parameter tuning.
---

# Retrieval quality and IR metrics

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

**The library defaults, against the published reference (nDCG@10).** Read this as "what the defaults
do", not as a claim about the engine — the comparison below is the one that measures it:

| Corpus | LexiSharp BM25, defaults | published reference (2021) | Δ |
|---|---:|---:|---:|
| NFCorpus (323 judged queries) | 0.308 | 0.325 | −5.2 % |
| SciFact (300 judged queries) | 0.662 | 0.665 | −0.5 % |
| ArguAna (1406 judged queries) | 0.289 | 0.315 | −8.3 % |

**Those deltas are a statement about the configuration, not about the engine, and the rest of this
page is the correction.** Each one compares two different systems: the left column is BM25 under
`Tokenizer.Default` (no stemming, stop words kept) at k1=1.5/b=0.75; the right column is BM25 under
the conventional English analysis at k1=0.9/b=0.4. Measured on the same corpora, with the analysis, the
parameters, the gain convention and the repeated-term rule aligned:

| Corpus | LexiSharp, aligned | published reference | Δ at matched parameters |
|---|---:|---:|---:|
| NFCorpus | 0.3215 at k1=0.9, b=0.4 | 0.3218 | −0.0003 |
| SciFact | 0.6788 at k1=0.9, b=0.4 | 0.6789 | −0.0001 |
| ArguAna | 0.364 at k1=0.9, b=0.4 | 0.3970 at k1=0.9, b=0.4 | **−0.033, cause not established** |

Every row is at the reference's own k1 and b. That matters for what may be concluded from the table,
and it is worth being explicit about the trap: **at its own defaults this library reads higher on all
three corpora** — 0.327, 0.692 and 0.444 against 0.3218, 0.6789 and 0.397 — and that is *not* a claim
that the published figures are beaten. Those two numbers are produced at different operating points,
and reading the second as the first is the same mistake that put a withdrawn figure in this file. The
aligned column is the comparison that means anything; the defaults are quoted in each pin's note.

At the published operating point, NFCorpus and SciFact reproduce the reference to the fourth
decimal. The engine was never behind on those two; the comparison was measuring the analyzer.

**ArguAna was measuring a defect.** The table above used to read 0.219 here, a deficit of 0.178, and
this page said the cause was not established. It was established as soon as somebody asked where the
number came from: `SearchOptions.ExcludedDocumentIds` was never applied on the term-at-a-time path,
which is the one this corpus takes. ArguAna queries are documents — 1,298 of the 1,406 test queries
have their id in the corpus, and the query text is that document's own text — so the document being
excluded was the lexically closest thing to the query and held rank 1. With the gate applied, at the
reference's own k1 and b: **0.364 against 0.3970**.

That also retires a number this page repeated. "+0.0012 for the exclusion" was this repository's own
measurement of an option that did nothing, which is why it looked negligible. It was worth +0.093.

The remaining 0.033 sits at a parameter choice, and the measurements below say as much — though not
more than that.

**At the library's own default parameters this corpus is not behind.** With the exclusion applied and
the repeated-term setting on, 1,406 queries, English analysis, linear gains:

| | k1=0.9, b=0.4 — the reference's operating point | k1=1.5, b=0.75 — this library's default |
|---|---:|---:|
| `QueryTermWeighting.Distinct` | 0.290 | 0.381 |
| `QueryTermWeighting.QueryFrequency` | 0.364 | **0.444** |

against a published 0.397. So the comparison is now true both ways, and neither half alone is the
story: at the reference's own k1 and b this library reads 0.364 and is 0.033 behind; at its own
defaults it reads **0.444 and is 0.047 ahead**. ArguAna's score is steep in k1 here — 0.290 to 0.381
on the repeated-term setting alone, from k1 alone — so 0.397 is a figure produced at a point that
looks like a poor operating point for this corpus. **Whether the reference would also read higher at
k1=1.5 is not something this repository can test**, and until it is, the aligned comparison stands as
the honest one and the deficit at that point is recorded rather than explained away.

**What was ruled out, by measurement.** The stop list: `StopWords.EnglishFunction` is word-for-word
the reference's 33. The idf: `ln(1 + (N − df + 0.5) / (df + 0.5))` is the reference's formula. The
title field: indexed, scored, counted in the document length, term frequencies summed — the index is
equivalent to the concatenation the reference uses. Query or document truncation: there is none.

**The tokenization difference is real, confirmed, and not the cause.** This index holds 1.12 % more
terms than the reference, and the excess appears on all three corpora — NFCorpus 2.77 %, SciFact
1.50 % — always in the same direction, always inside the 3 % tolerance. Two rules account for it, both
seen by inspecting the tokenizer:

- *Contractions.* The reference keeps an apostrophe inside a word, so `don't` is one token; this one
  produces two, a stem and a single-character fragment, and `it's` yields only the fragment `s`.
- *Hyphens.* The same for a hyphen between two letters, so `environment-friendly` is one token there
  and two here. 7,717 hyphenated words in this corpus.

Measured end to end at matched parameters with the exclusion applied: **0.364** as shipped, **0.362**
with the apostrophes joined, **0.367** with the hyphens joined. Aligning both analyses is worth about
+0.003 of the 0.033. Both differences are larger than the 0.39 % excess left after the apostrophes,
so some compensating difference exists that has not been identified. Changing the tokenizer would
invalidate every recorded baseline here, so it is not done.

**The same conclusion at top-k 100, and a sharper signature.** The reference configuration publishes
recall as well as nDCG@10, and the harness measures it, so the comparison can be made at both ends of
the ranking. At k1=0.9, b=0.4 with the exclusion applied, on the same 1,406 queries:

| | LexiSharp | published reference | Δ |
|---|---:|---:|---:|
| nDCG@10 | 0.364 | 0.3970 | −0.033 |
| **R@100** | **0.843** | **0.9324** | **−0.089** |

The reference finds the gold document inside the top 100 for 93 % of these queries; this library
does for 84 %. **The deficit is a retrieval difference, not a ranking one** — it is not documents
being ordered differently near the cutoff, it is documents not being found at all. And the
tokenization variants do not close it either: joining hyphens gives R@100 0.841 and joining both
rules 0.839, against 0.843 as shipped. So the two measures agree, and the answer to "which rule
differs" is that no rule found so far accounts for it.

That is the state of it, stated plainly rather than dressed up: the exclusion defect was most of the
0.126, the parameters are not it, the analyzer is not it by two independent measurements, and 0.089 of
recall is unexplained. Whoever picks this up should start from which documents are matchable rather
than from where they land.

**The failing queries are not unmatchable, which narrows it further.** Of the 216 queries whose gold
document does not reach the top 100, **every one shares at least one analysed term** with it. Zero are
lexically unreachable. So the documents are findable in principle and are not being found. Five of the
1,406 test qrels point at a document id that is not in the corpus at all, so 1,401 queries are usable
and nobody can retrieve those five — which does not explain a gap of this size either.

**The stemmer is not it, measured three ways.** R@100 with no stemming at all, with this repository's
`PorterStemmer`, and with a second Porter written from the published algorithm rather than from this
codebase: 0.842, 0.846, 0.846. The third exists so the comparison does not rest on trusting a
from-memory reimplementation of the reference's stemmer — if a different Porter moves nothing, the
stemmer is not the cause whichever one is correct. The b curve is short at every point: 0.790 at b=0,
0.843 at b=0.4, 0.868 at b=0.75, against 0.9324 published.

**The published figure for this corpus is not one number, and the spread is wider than the gap.** The
2021 BEIR paper reports BM25 on ArguAna at **0.315**, indexing *"the title (if available) and passage
of each document as separate text fields"* through Elasticsearch's defaults. Anserini's regression
reports **0.3970** for the same corpus, indexing *"in a 'flat' manner, by concatenating the title and
text into the contents field"* — which is what this harness does, checked against the reproduction's
own docgen template. Same corpus, same metric, same k1 and b, and **0.082 apart**, because a flat
index and separate fields are not the same index. This library's aligned 0.364 falls between the two:
0.049 above the 2021 figure, 0.033 below the regression. A claim about matching "the published
ArguAna number" therefore depends on which one is meant; the table above states the comparison this
harness can actually support.

**What would close it, and why it was not done here.** Attributing the remainder needs the
reference's own run file — the trec_run ranking all 1,406 queries — and the reproduction publishes no
results directory, only commands, configs and docgen. Without it the gap can be bounded and
characterised, as above, and not attributed. That is the one thing still worth doing, and it needs
the file rather than more argument.

**Why a small difference can be worth this much.** The gold document is outside the top 10 for
**39.9 %** of these queries, and the score gap between rank 10 and rank 11 has a tenth percentile of
**0.118 %** — a fifth of the queries are decided by less than 0.046 %. Moving 0.033 of nDCG is roughly
165 documents promoted from outside the top 10 into it.

**ArguAna: this page said the gap was closed, and that was wrong.** It reported **0.4061** at
k1=3.0/b=0.75, above the published 0.3970, and explained the difference as a scoring rule: the
library deduplicated query terms, the reference scores one clause per query-token occurrence, and on
a corpus whose every test query is a whole ~200-word argument, counting them was said to be worth
**+0.0705** (0.2197 to 0.2902 at k1=0.9/b=0.4).

Re-measured on 1,406 queries with the English analysis, at the reference's own k1=0.9/b=0.4:

| setting | nDCG@10 |
|---|---:|
| `QueryTermWeighting.Distinct` (the default) | 0.219 |
| `QueryTermWeighting.QueryFrequency` | 0.271 |
| **effect of the setting** | **+0.052** |

Three conventions were checked, and the third one is the defect. The linear gain convention returns
the same 0.271 as the exponential one. Excluding the query document was measured as worth +0.0012 by
this repository's own measurement — a number that was wrong, because the option was inert: it is
worth **+0.093** once applied. Neither 0.2902 nor 0.4061 is reproducible, and the 0.271 above is
measured without the exclusion, as the other two were.

**No release carried either figure.** `QueryTermWeighting` is absent from the `v0.6.0` tag, which
predates the commit that added it, and 0.4061 is absent from the 0.6.0 documents entirely
(`git show v0.6.0:docs/evaluation.md | grep -c 0.4061` is 0). So the claim was withdrawn here,
before 0.7.0, rather than in a release note: no published release ever contained it.

The reason is structural, and that is the part worth keeping. The harness flag fed exactly one
thing - the `QueryTermWeighting` argument of `new Bm25Scorer(...)` - and that scorer went into
`RankedTextSearchEngine`, which deduplicated the query before the scorer could see a repetition. The
two settings could not have produced different rankings, so those figures could not have come from
the code that published them. The engine now hands the scorer the raw term list, the setting is
observable through `Search`, and `QueryTermWeightingTests` fails if it ever stops being.

**What is left on ArguAna is 0.033, and it is not accounted for.** At matched parameters this
library reads 0.364 where the reference publishes 0.3970. The query-frequency setting is part of that
column and the excluded-ids fix is most of the rest; what remains is measured to be a difference in
how the two analyses tokenize, described above, and it has not been shown to be the cause of the
score difference — only that the two indexes differ by 1.12 % in terms and 3.4 % in single-character
tokens. ArguAna's k1 sensitivity also runs opposite to the other two corpora, so comparing this
library at k1=1.5/b=0.75 against a reference figure produced at k1=0.9/b=0.4 is not a comparison at
all, which is how a flattering number got in.

The reference is also **newer than the table it is first compared against**: a current per-corpus
regression for this exact index puts ArguAna at 0.3970, not the 0.315 of the 2021 paper.

Reproduce the alignment, and check it against pinned values:

```bash
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset nfcorpus --no-tuned \
  --analyzer english --ndcg-gain linear --reference-bm25 0.9,0.4 --query-term-frequency
dotnet run --project bench/LexiSharp.Eval -c Release -- --verify-reference
```

See [the harness README](../bench/LexiSharp.Eval/README.md) for what each flag changes and
`reference/pinned.json` for the measured values.

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
  [Tokenizer customization](indexing.md#tokenizer-customization). What that comparison leaves out is
  the stop word list, which is the larger half of the analysis: `--analyzer english` (Porter plus
  the conventional 33 words) takes SciFact to 0.692 and NFCorpus to 0.327;
- ArguAna's BM25F 0.344 is **untested, not a result**: it is default-vs-default, and a tuned
  BM25 over its 1406 queries did not fit the compute available here.

## Metrics

`RetrievalMetrics` covers the standard set, so a number means here what it means in the
literature:

| Metric | The question it answers |
|---|---|
| `Precision@k`, `Recall@k`, `F1@k` | how much of the page was right, how much of the truth was found |
| `nDCG@k`, binary and **graded**, both gain conventions | is the right document near the top, weighted by its grade |
| `ReciprocalRank@k` (→ MRR) | how high the first relevant document appears |
| `AveragePrecision@k` (→ MAP) | precision at every rank where a relevant document appears |

Relevance grades come from the qrels file. A query carrying no judgement is loaded and
counted in the run, but excluded from the averages rather than scored as a zero — silently
averaging in a zero is how a harness flatters itself.

**nDCG has two conventions and only one of them is the published one.** `NdcgAtK` defaults to
exponential gains (`2^rel − 1`); the standard evaluation tool, and therefore the published BM25
figures, set the gain to the relevance level itself. They agree on binary relevance and
disagree on a graded corpus: NFCorpus scores 0.3080 exponential against 0.3071 linear for the same
run. Pass `--ndcg-gain linear` before quoting a number next to a published one.

## Tuning BM25, BM25F, BM25+ and BM25L

Let the corpus pick its own parameters. `Bm25ParameterTuner` grid-searches `k1` and `b`
against your own validation queries, judged by `Precision@k`, `Recall@k`, `F1@k` or
`nDCG@k`; `Bm25FParameterTuner` does the same for BM25F's `k1`/`b` and its per-field
weights, and adds one question of its own. `Bm25PlusParameterTuner` and
`Bm25LParameterTuner` search the single-field variants' third parameter, the `delta` lower
bound, over the full `k1 × b × delta` product.

| Tuner | Searches | Default points | Reports |
|---|---|---|---|
| `Bm25ParameterTuner` | `k1 × b` | 25 | — |
| `Bm25FParameterTuner` | `(k1, b)`, then field weights | 25 + Π weights | `WeightingHelped` |
| `Bm25PlusParameterTuner` | `k1 × b × delta` | 125 | `DeltaHelped` |
| `Bm25LParameterTuner` | `k1 × b × delta` | 125 | `DeltaHelped` |

**A tuned score is an oracle, not a fair baseline.** The winner was fitted on the same
queries it is scored on, and the more configurations were tried, the more of the gain is
fitting noise. Score it on a held-out set, or treat it as an upper bound. Every comparison
in this documentation that claims a scorer "beats" another was run tuned-against-tuned for
exactly this reason — a default-vs-default comparison mostly measures which default fits
the corpus.

**State the objective, and check it is not flat.** Every tuner maximizes the metric you name, and
the default is `F1@k`. On the reference corpus `F1@5` is **0.3588 for all six configurations** — a
flat objective gives a tuner no signal, so its "winner" is whichever grid point the tie-break
reached first, and a tuned row built on it is a number that means nothing. Two figures in this
repository's history were quietly wrong for that reason alone: BM25F-tuned and BM25-tuned both read
0.8812, and re-fitting both on the nDCG they were being reported in moved them to 0.8867 and
0.8978. The `lexisharp benchmark` CLI takes `--metric ndcg` for exactly this, and prints the
objective it used.

**`DeltaHelped` is the answer to "does the lower bound help on my corpus".** It is `false` on the
reference corpus and on NFCorpus — where all three tuners settled on identical parameters
(`k1=2, b=0.5, δ=0`) and an identical 0.311 nDCG@10, the δ search choosing no bound and handing
back BM25. On the reference corpus `false` is the weaker of the two reasons: 46 of the 125 points tie
at the maximum and every δ from 0 to 1 is among them, so δ = 0 is what the tie-break reached rather
than a preference — that corpus cannot separate the formulas at all. It is `true` on SciFact, by
0.002 and 0.004 nDCG@10 at `δ=0.25`, fitted in-sample on the 300 queries that chose it. The harness
prints each variant's own unfloored score beside it, and on SciFact that baseline reads **0.664 —
identical to `BM25 tuned`**, so the two 125-point searches agree with `Bm25ParameterTuner` on the
no-bound slice and the margin is δ's rather than the price of a wider grid. It is still a 0.004 lead
fitted in-sample over 125 candidates, so an upper bound rather than a result. `0.0` belongs in
`deltaValues` for the other reason: without a no-bound candidate the search cannot conclude that
nothing helps, and `UnflooredMetricScore` is `NaN` rather than a fabricated comparison.

**Both variants degenerate to BM25 at `δ = 0`, which is what makes the tables readable.** Obvious for
BM25+, whose `δ` is additive. For BM25L it is not, and used to be documented here as *not* holding —
its denominator carries the compressed frequency where BM25's carries the raw one. The compression is
on the numerator too, so it cancels: `ctd / (k1 + ctd) = tf / (k1·norm + tf)`, BM25's term weight. A
`δ = 0` grid therefore returns `Bm25ParameterTuner`'s own winner and score, which
`AZeroDeltaGridReproducesTheBm25ParameterTunerExactly` asserts across all three tuners.

```csharp
var plus = new Bm25PlusParameterTuner(index, validationQueries).Tune(topK: 10, metric: TuningMetric.Ndcg);

if (plus.DeltaHelped)
    Console.WriteLine($"delta {plus.Delta} beat no delta; k1={plus.K1} b={plus.B}");
else
    Console.WriteLine($"no delta beat no delta; best was k1={plus.K1} b={plus.B}");

var plusEngine = new RankedTextSearchEngine(index, new Bm25PlusScorer(plus.K1, plus.B, plus.Delta));
var lEngine = new RankedTextSearchEngine(
    index, new Bm25LScorer(l.K1, l.B, l.Delta));
```

At 125 points the δ tuners cost five times a BM25 search, so both are capped at 512 configurations
and refuse above it with the count rather than trimming the grid.

`Bm25FParameterTuner` grid-searches `(k1, b)` and the weight of each named field, in two stages:
`(k1, b)` first with fields neutral, then the weights at that winner. That is coordinate descent,
not an exhaustive product, and the class says so — a configuration that is only good jointly can be
missed. The δ tuners have no such caveat: their product *is* searched in full. The run is refused
above a configuration cap, with the count, rather than silently trimmed.

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
  queries), `tfidf`, `ql` (query likelihood), `hybrid` (RRF over BM25 + TF-IDF),
  `bm25+`/`bm25l` (the variants at a fixed `delta`) and `bm25+-tuned`/`bm25l-tuned` (fitted
  on `(k1, b, delta)`), `bm25f`, `bm25f-title`, `bm25f-tuned`, the three `bm25-proximity*`
  shapes, and `bm25-semantic`.
- `--metric <precision|recall|f1|ndcg>` chooses what every `*-tuned` configuration maximizes.
  The default is `f1`, and **a flat objective makes a tuned row meaningless** — see the tuning
  section above. Pass the metric you intend to report. The objective actually used is echoed in the
  run's output.
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
