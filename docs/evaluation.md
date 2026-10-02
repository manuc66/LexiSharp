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

**Two configurations, and what each one answers.** Both are this library's BM25 over the same
three corpora, scored on the same runs. They differ in analysis and parameters, and the
published figures were produced with the second — which is why the first is not a like-for-like
comparison. nDCG@10:

| Corpus | Analysis and parameters | LexiSharp | published reference (2021) | Δ |
|---|---|---:|---:|---:|
| NFCorpus | defaults: `Tokenizer.Default`, k1=1.5, b=0.75 | 0.308 | 0.325 | −5.2 % |
| SciFact | defaults | 0.662 | 0.665 | −0.5 % |
| ArguAna | defaults | 0.320 | 0.315 | +1.6 % |
| NFCorpus | aligned: English analysis, k1=0.9, b=0.4 | 0.3215 | 0.3218 | −0.0003 |
| SciFact | aligned | 0.6788 | 0.6789 | −0.0001 |
| ArguAna | aligned | 0.3970 | 0.3970 at k1=0.9, b=0.4 | 0.0000 |

The **defaults** rows answer *what the library does out of the box*: no stemming, stop words
kept. The **aligned** rows answer *is this comparable to the published figure* — and with the
analysis, the parameters, the gain convention and the repeated-term rule matched, the two agree
to the fourth decimal on NFCorpus and SciFact and are identical on ArguAna. The gap between the
two row groups is configuration, not ranking.

**ArguAna: the metric is reproduced, and so are the raw scores.** The two are verified by different
means and the distinction is the point.

The aggregate is verified without this repository's own metric code being involved. `trec_eval` 9.0.8,
run on a `trec_run` this harness writes at that configuration, reads nDCG@10 **0.3970** and recall@100
**0.9324** — the two published figures, to four decimals. This harness reads 0.3970 on the same run.
The metrics are computed differently and agree; where they do not, the difference is 0.0004, confined
to the 102 of 1,406 queries whose returned scores contain a tie. A tie is the plausible mechanism,
being the only property those 102 queries share, and it is not established: neither the order written
in the run file, nor document id, nor a reversal of the tied group reproduces `trec_eval`'s reading.
Which order produced the published figure is not established either.

One thing is worth knowing about reading a metric from this harness, because it changed a number
that looked like a ranking result. The harness measures at **rank 10**, not at the depth the run
retrieved, and the two being the same value is a coincidence of the default: a bit-for-bit
reproduction run retrieves one document deeper than it measures, so measuring at the retrieval depth
reported nDCG@11 under a heading reading nDCG@10. Retrieval depth and evaluation depth are different
things, and the cutoff is named (`MetricDepth`) and independent of `--top-k`. The table below
holds the full set of figures the published config produces, at the cutoff:

| | LexiSharp | reference | Δ |
|---|---:|---:|---:|
| nDCG@10 | 0.3970 | 0.3970 | 0.0000 |
| nDCG@5 | 0.3445 | 0.3445 | 0.0000 |
| recall@100 | 0.9324 | 0.9324 | 0.0000 |
| recall@1000 | 0.9872 | 0.9872 | 0.0000 |
| MAP@100 | 0.3280 | 0.3280 | 0.0000 |
| reciprocal rank | 0.3282 | 0.3282 | 0.0000 |

**What is reproduced is the scores, and on the raw bits.** A metric sees an order, and an order can
agree while every number behind it differs — which is exactly what was happening. Against a run produced
by the reference implementation's own searcher over its own index, for all 1,406 test queries:

| | paired scores identical on the raw bits |
|---|---:|
| index built over every document (as it was) | 0 / 14,168 |
| inverse document frequency over the documents carrying the field | 876 / 14,168 — 6.18 % |
| … and the reference's scale, precision and rounding | **14,168 / 14,168 — 100.00 %** |

At the depth the comparison runs, one document deeper than the metric is measured at, the count is
**15,466 / 15,466** over all 1,406 queries.

The three differences that got there, each behind an option whose default is unchanged:

| difference | option | default | what it is |
|---|---|---|---|
| idf over 8,674 documents instead of 8,673 | `ITextIndex.StatisticDocumentCount` | every document | the corpus holds one document with no term; worth 2.6e-05 on every score |
| no `k1 + 1` in the numerator | `Bm25Scorer(saturationConstant: false)` | `true` | the reference's BM25 has no saturation constant; same ranking, factor of 1.9 off |
| single precision, reciprocal table, total rounded once | `Bm25Scorer(arithmetic: SinglePrecision)` | `Double` | same formula, different arithmetic; worth about one part in 10⁷ |
| scores rounded to four decimals, ties walked down | `SearchOptions.ScoreRounding` | `None` | what the reference does to a score before writing it down; worth up to 5e-06 |

The last of the four is the one a reader is most likely to think is a fudge, so it is worth being plain
about: it is a post-processing step, not scoring, and it is lossy — it merges scores that differ by less
than half a ten-thousandth and then separates them again by rank, so the same document reports two
different scores at two different page sizes. It is here because a run file contains those numbers and
not the scorer's, and reproducing the arithmetic without reproducing what is written down reproduces
nothing anybody can check.

That measurement was nDCG@10 identical to the last digit on **all 1,406 queries, one by one**: paired
difference 0.00000000, standard deviation 0.00000000, 95 % interval [0.00000000, 0.00000000]. The two
indexes agree **term for term**: 23,895 distinct terms and 969,528 occurrences on each side, zero terms differing
in document frequency, zero in term frequency, and zero terms present in one index and not the other.

The comparison runs against the reference's own index, read out of it rather than re-derived from an
assumption about its analyzer, and against its own evaluation binary.

**What the 0.033 was.** Not a parameter choice and not the corpus — four rules, each measured by asking
the reference implementation rather than by reasoning about it, and each behind an option whose default
is unchanged:

| rule | option | default | measured effect |
|---|---|---|---|
| phrase syntax on prose | `SearchOptions.ParseQuerySyntax` | `true` | 138 of 1,406 queries returned 0–1 results at any depth |
| repeated query terms | `QueryTermWeighting.QueryFrequency` | `Distinct` | +0.074 |
| word boundaries | `TokenizerOptions.WordSegmentation` | `Flat` | 18 separators × digit/letter on both sides |
| possessive, before the stop list | `TokenizerOptions.StripPossessives` | `false` | `it` 3,501 → 3,134; `that` and `there` removed |
| diacritics kept | `TokenizerOptions.FoldDiacritics` | `true` | ±0.003 |
| U+0130 folded | `TokenizerOptions.FoldTurkishDottedI` | `false` | one term in 23,895 |
| one-byte lengths | `InMemoryTextIndex.DocumentLengthQuantization` | `Exact` | −0.006, kept as a reproduction device |
| idf over the documents carrying the field | `ITextIndex.StatisticDocumentCount` | every document | 2.6e-05 on every score, no effect on any ranking |

Three findings here were counter-intuitive enough to be worth stating, because each is the kind of
thing that gets "fixed" the wrong way:

- **The possessive rule has to run before the stop word list, not inside the stemmer.** Trimming inside
  the stemmer is too late: the list has been tested on `it's` rather than on `it`, so `it's` survives as
  a stop word and only then becomes `it`. On this corpus that was an `it` 3,501 times against the
  reference's 3,134, plus a `that` (83) and a `there` (36) the reference has not got at all — 486 of
  the 487 occurrences by which the two indexes differed. The reference's `it` comes from `its`, which
  is not a possessive and is not on the list.
- **An unconditional joiner belongs to the token even at its start.** `_630888` indexes whole and
  `__alpha` as `__alpha`, while `-alpha` is just `alpha` — so it is the underscore's rule, not a rule
  about any separator, and a run of underscores alone is not a token.
- **A combining mark ended the word it followed.** `celi\u0307l` split into `celi` and `l` into two
  tokens. A mark modifies the character it follows, so it extends a word and cannot open one — measured
  across all four positions: `a\u0307b` and `a\u0307` are each one token, `\u0307ab` is just `ab`, a lone
  mark is nothing. ArguAna holds no combining mark, so it moved no number here; NFCorpus holds four
  characters out of 5,779,318.
- **The stemmer refuses every term containing a non-ASCII character.** The algorithm is defined
  over characters, not scripts: everything outside `aeiou` is a consonant. Asking the reference's own
  stemmer settled it — `they’re` → `they’r`, `naïve` → `naïv`, `façade` → `façad` — and its answers are
  this library's. That same stemmer does **not** remove possessives: asked directly, it answers
  `adam'`, which makes the possessive removal a separate stage.

**No difference remains.** Of the 505 code points whose lowercase differs from themselves over the
ranges a European corpus contains, U+0130 is the *only* one where .NET's invariant casing and the
reference disagree — so it is a single character, not a difference of strategy. And the fold is the
Unicode **simple** mapping, U+0069, rather than the full one that appends U+0307: their index holds a
five-character `celil`, and handing their analyzer a six-character `celi\u0307l` returns the dot intact,
which means their pipeline never produced one. Folding to the full mapping, on the reading that "fold it
to i" wants the complete Unicode answer, is a different function and does not reproduce it.

Leaving the character alone is still defensible — it is the right answer for a Turkish index, where `İ`
and `i` are different letters and folding them merges two words — which is why it is an option and why the
default is unchanged.

Both constructions were measured here rather than reasoned about: the multi-field run scores the title
and the body as separate fields with their own document frequencies and their own length, then sums
the two scores, which is what a multi-field Lucene query does. It gives nDCG@10 0.370 and R@100
0.802 — better than flat on nDCG@10 and worse on recall, and short of 0.414 either way.

The figures read out of the leaderboard paper are its Table 1 row for ArguAna, whose header names the
two metric groups. The same paper contains a second table with a different column arrangement whose
ArguAna row does not reconcile with the first, which is why the citation is to the row and its header
rather than to a bare number.

**Why a small difference can be worth this much.** The gold document is outside the top 10 for
**39.9 %** of these queries, and the score gap between rank 10 and rank 11 has a tenth percentile of
**0.118 %** — a fifth of the queries are decided by less than 0.046 %. The 0.033 is therefore about 165
documents promoted from outside the top 10 into it, which is a difference an aggregate score does not
surface: the mean moves by the size of the gap, not by how many queries it decided.

**The withdrawn ArguAna figure.** **0.4061** was reported at k1=3.0/b=0.75, above the published
0.3970, and explained as a scoring rule: the library deduplicated query terms, the reference scores one
clause per query-token occurrence, and on a corpus whose every test query is a whole ~200-word
argument, counting them was said to be worth **+0.0705** (0.2197 to 0.2902 at k1=0.9/b=0.4). It is
withdrawn, and no published release ever carried it — see the
[changelog](../CHANGELOG.md#withdrawn) for the provenance.

Re-measured on 1,406 queries with the English analysis, at the reference's own k1=0.9/b=0.4:

| setting | nDCG@10 |
|---|---:|
| `QueryTermWeighting.Distinct` (the default) | 0.240 |
| `QueryTermWeighting.QueryFrequency` | 0.301 |
| **effect of the setting** | **+0.061** |

Three conventions were checked, and the third one is the defect. The linear gain convention returns
the same 0.301 as the exponential one. Excluding the query document is measured separately, at the
pin's own configuration (English analysis, k1=1.5/b=0.75, linear): **0.313 without, 0.4180 with, i.e.
+0.105** — the pin value is recorded with the flag on. An earlier reading of +0.0012 measured the
option doing nothing (the exclusion was not applied on the term-at-a-time path); the fix is what made
it reach its document. Neither 0.2902 nor 0.4061 is reproducible, and the 0.301 above is measured
without the exclusion.

The reason the withdrawn figures could not have come from this code is structural. The harness flag fed
exactly one thing — the `QueryTermWeighting` argument of `new Bm25Scorer(...)` — and that scorer went
into `RankedTextSearchEngine`, which deduplicated the query before the scorer could see a repetition. The
two settings could not have produced different rankings. The engine hands the scorer the raw term
list, the setting is observable through `Search`, and `QueryTermWeightingTests` fails if it ever stops
being.

**There is nothing left on ArguAna.** At matched parameters this library reads 0.3970 where the
reference publishes 0.3970, identically on all 1,406 queries. The analysis rules that accounted for it
are set out above, and each is behind an option whose default is unchanged — so `--analyzer english`
with the query document excluded reads 0.4180, and the gap above the 0.3970 parity is a difference of
analysis convention rather than of ranking.

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
  0.662 → 0.687, and is mixed on ArguAna 0.320 → 0.308 (BM25) / 0.302 → 0.312 (QL). That is why it stays opt-in — see
  [Tokenizer customization](indexing.md#tokenizer-customization). What that comparison leaves out is
  the stop word list, which is the larger half of the analysis: `--analyzer english` (Porter plus
  the conventional 33 words) takes SciFact to 0.692 and NFCorpus to 0.327;
- ArguAna's BM25F 0.379 is **untested, not a result**: it is default-vs-default, and a tuned
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
reached first, and a tuned row built on it is a number that means nothing. Fitting two scorers on
that flat objective is how BM25F-tuned and BM25-tuned both came out at 0.8812; re-fitted on the
nDCG they are reported in, they are 0.8867 and 0.8978. The `lexisharp benchmark` CLI takes
`--metric ndcg` for exactly this, and prints the objective it used.

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

**Both variants degenerate to BM25 at `δ = 0`, which is what makes the tables readable** — obvious for
BM25+, whose `δ` is additive, and for BM25L a cancellation in the term weight
([worked through in BM25 variants](ranking.md#tuning-delta-and-what-it-does-to-the-comparison)). A
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

**Without a field-aware index it refuses, by name.** Scoring against an index that is not an
`IFieldStatisticsIndex` throws `NotSupportedException` naming the index rather than ranking
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

## Evaluating a live engine, on demand

`CorpusBenchmark` evaluates *configurations* over a corpus snapshot it indexes itself. For the
engine an application is actually serving — with whatever it currently holds, incremented by
real writes — `RetrievalEvaluator` runs a fixed panel of labeled queries against that live
engine and reports the metrics at a depth:

```csharp
using LexiSharp.Benchmarking;

var panel = new[]
{
    new BenchmarkQuery("q1", "how do I renew an expired token", ["doc-17", "doc-92"]),
    new BenchmarkQuery("q2", "refresh policy",            ["doc-17"]),
};

var evaluator = new RetrievalEvaluator(panel, topK: 10);
PanelEvaluationResult result = evaluator.Evaluate(engine);
```

Call it periodically against the same panel and compare successive results: a panel metric
moving is the relevance half of data-drift observability. `PanelEvaluationResult` carries the
means (`Metrics`, the same `BenchmarkMetrics` shape a CLI run reports) and the per-query rows
(`ByQuery`, each with the retrieved ids, scores and the 1-based rank of the first judged
document), so a movement can be attributed to one query rather than to the mean.

The panel is the app's own: relevance comes from the judgments it pinned, and every query is
searched with the caller's options except `Limit`, which is pinned to the metric depth — the
retrieval depth and the evaluation depth stay one number. A query without judgments is loaded
but excluded from the averages; a panel with none reports zeros, not a measurement. Graded
judgments keep the library's exponential nDCG convention by default, with
`NdcgGain.Linear` available for comparing against a linear-convention figure.

## Performance

Latency and allocation numbers live in [Benchmarks](benchmarks.md): BenchmarkDotNet runs on
one named machine, with its configuration and the reproduce command. Read them as
indicative and re-run the suite on your own hardware. Of the two kinds of number there, the
one that travels is **allocation** (counted in bytes, host-independent); wall-clock means
from a shared or contended machine are not publishable, and none are quoted from CI.
What *does* gate a build is deterministic and clock-free: `lexisharp verify`, which replays
the reference corpus against a committed baseline.
