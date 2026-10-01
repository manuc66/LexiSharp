---
title: BM25 and ranking functions
nav_order: 5
description: >-
  BM25 and its variants — BM25+, BM25L, BM25F — plus TF-IDF, query likelihood and
  boolean scorers, score boosting, proximity, and per-term score explanations.
---

# BM25 and ranking functions

Ranking is a strategy object: `ITextScorer` reads the index and returns a score, and the
engine does everything else. Swap the scorer, keep the index.

- **`Bm25Scorer`** — Okapi BM25, with ready-made `Bm25Parameters` profiles (`Balanced`,
  `Aggressive`, `Conservative`).
- **`TfIdfScorer`** — TF-IDF.
- **`QueryLikelihoodScorer`** — probabilistic language model (Jelinek-Mercer smoothing).
- **`BooleanScorer`** — exact AND/OR filter.
- **`Bm25PlusScorer` / `Bm25LScorer`** — the published BM25+ (Lv & Zhai 2011) and BM25L
  (Lv et al. 2006) variants, each with a lower bound `delta` on the term frequency.
  `Bm25PlusParameterTuner` / `Bm25LParameterTuner` search that `delta` alongside
  `(k1, b)`.
- **`Bm25FScorer`** — the same `ITextSearchEngine` contract over documents that declare
  `TextFields`: per-field weights, per-field length normalization, an explainable
  per-term breakdown. Requires an index that tracks fields.
- **Score boosting** — `BoostedTextSearchEngine` applies **signed** adjustments
  (multiplicative factor and/or additive offset) per result, without touching the engine
  underneath.

**The measurements are not all flattering, and publishing them is the point.** Each of the
three measured sections below ends with what it actually scored on the corpora tried here:
tuned on their own `δ`, the BM25 variants **tie** a tuned BM25 on the reference corpus and
NFCorpus and edge it by 0.002–0.004 on SciFact — an in-sample margin, not a result to quote;
BM25F has **no** measured case of beating a tuned BM25, and its tuner reports
`WeightingHelped = false` on every corpus it has been run on; proximity is neutral at quarter
strength and negative at full strength. On these corpora, fitting parameters is worth more than
adding a scorer — see [Evaluation](evaluation.md).

## Conventions, not mathematics

`Bm25Scorer`'s constructor takes three arguments that are not part of the formula. Each exists
because a published figure or a recorded run was produced under one of them and not the other, so
each is an option and each defaults to this library's own convention. Passing one is how you
reproduce a *specific* figure, not how you get a better one — and none of them is recommended for
ordinary use.

| | default | what it changes |
|---|---|---|
| `saturationConstant: false` | `true` | whether the numerator carries `k1 + 1` or 1. It scales every score of a query by the same factor, so it cannot change a ranking — but a score is what gets compared. |
| `arithmetic: Bm25Arithmetic.SinglePrecision` | `Double` | computes per-term contributions and a reciprocal table in `float` rather than `double`. Narrower, and slower: the table is 256 entries rebuilt whenever the corpus's average length moves. |
| `ITextIndex.StatisticDocumentCount` | `Count` | which documents the `N` of the idf counts. Default is every indexed document; the value is the number carrying the field, which is what the reference implementation uses. On ArguAna they differ by one — the corpus holds a document with no term at all — and that one document made every idf on the corpus wrong. |

`StatisticDocumentCount` is a default-implemented member of `ITextIndex`, so an existing index
implementation keeps compiling and keeps its current behaviour. It matters for the four scorers
that build an idf — `Bm25Scorer`, `Bm25PlusScorer`, `Bm25LScorer`, `Bm25FScorer`, `TfIdfScorer`,
`BooleanScorer` and `ProximityReranker` all read it.

### Two more, which are not on the table

Two switches a bit-for-bit comparison needs are **internal**, reachable by the evaluation harness
and nothing else, and they are absent from the table above on purpose. Both are things a public
option should not be, for different reasons.

`DocumentLengthQuantization` does not report a length more or less precisely — it reports a
*different quantity*. `OneByte` stores a length the way an index storing one byte per document
does, which is what makes a 149-term and a 151-term document score identically under BM25 length
normalization. Nothing at a call site says which of the two quantities it is comparing against, so
a public option here would let a caller build a dependency on a unit whose meaning depends on how
the index was constructed. That surfaces as a wrong number rather than as a compile error.

`ScoreRounding` is not arithmetic at all: it rounds returned scores to four decimals and walks down
each run of near-equal scores by a millionth, which is what one reference does before writing a run
file. It is lossy by construction — it merges scores differing by less than half a ten-thousandth,
and it makes one document's returned score depend on which others came back beside it, so the same
query against the same index reports two different scores at two different page sizes. An option
named `ScoreRounding` invites the belief that it affects presentation; it does not, because a metric
measured on a rounded run measures a different ranking function. It is reached through a method
rather than a constructor parameter for the same reason: naming the call is the signal that
something other than searching is being asked for.

With `saturationConstant: false`, single precision, reference statistics and the rounding, this
library's raw scores match one reference implementation's on the raw bits for all 15,466
(query, document) pairs of the ArguAna test split, and the aggregate reaches its published nDCG@10
and recall@100 to the fourth decimal — the latter confirmed by `trec_eval` rather than by this
repository's own metric code. See [Evaluation](evaluation.md).

## BM25 variants

Two published single-field variants of BM25, both behind the same `ITextSearchEngine` contract, both
with an `IScoreExplainer` breakdown, a query plan, and a `delta` lower bound on the term frequency.

**BM25+** (Lv & Zhai 2011) adds a floor δ to the whole term weight — **outside** the fraction:

```
norm(t,d) = 1 − b + b·|d|/avgdl
score(q,d) = Σ_t  idf(t) · ( tf(t,d)·(k1 + 1) / (tf(t,d) + k1·norm(t,d)) + δ )
```

**BM25L** (Lv, *When documents are very long, BM25 fails!*, SIGIR 2011) compresses the term frequency
and uses **the compressed value in the denominator too**:

```
ctd(t,d) = tf(t,d) / norm(t,d)
score(q,d) = Σ_t  idf(t) · (k1 + 1)·(ctd(t,d) + δ) / (k1 + ctd(t,d) + δ)
```

**Both variants degenerate to `Bm25Scorer` at `delta = 0`** — trivially for BM25+, whose δ is
additive, and not obviously for BM25L, which is the subject of [the next
subsection](#tuning-delta-and-what-it-does-to-the-comparison). A test asserts the BM25+ equality bit
for bit, but it **cannot** see where δ sits, since a formula that shifted `tf` by δ inside the
fraction would satisfy it too. `Bm25PlusAddsDeltaOutsideTheFraction` is the test that discriminates,
and `Bm25LUsesTheCompressedDenominatorNotBm25s` does the same job for BM25L against BM25's
denominator. Both exist because the first version of this section got both wrong — see the
correction below.

One departure for BM25+: read literally, `idf(t)·δ` applies even to terms the document lacks, so the
paper's model scores non-matching documents above zero. Reference implementations handle that by
computing and subtracting a non-occurrence term (`bm25s` does exactly this). That conflicts with the
« score 0 means no match » convention, so this implementation only sums over terms the document
actually has. BM25L needs no such accommodation — its term weight is already 0 at `tf = 0`.

### Tuning `delta`, and what it does to the comparison

`Bm25ParameterTuner` searches `(k1, b)`. It cannot search `δ`, because it does not know about it —
which left the variant tables below reading as a verdict on the formula when they were mostly a
verdict on one number. `Bm25PlusParameterTuner` and `Bm25LParameterTuner` search the full
`k1 × b × delta` product (125 points by default, five times BM25's cost, which is why the run is
capped and the count reported rather than the grid silently trimmed):

```csharp
var plus = new Bm25PlusParameterTuner(index, validationQueries).Tune(topK: 10, metric: TuningMetric.Ndcg);
new Bm25PlusScorer(plus.K1, plus.B, plus.Delta);   // plus.DeltaHelped: did the bound earn its place?

var l = new Bm25LParameterTuner(index, validationQueries).Tune(topK: 10, metric: TuningMetric.Ndcg);
new Bm25LScorer(l.K1, l.B, l.Delta);
```

**Both variants degenerate to BM25 at `delta = 0`**, which is what makes the two tables
comparable. BM25+'s is obvious — the bound is additive. BM25L's is not, and used to be documented
here as *not* holding: its denominator carries the compressed frequency where BM25's carries the
raw one. The compression is on the numerator too, so it cancels:

```
ctd / (k1 + ctd) = (tf / norm) / (k1 + tf / norm) = tf / (k1·norm + tf)      ... BM25's term weight
```

So `delta = 0` is plain BM25, term for term, and a `delta = 0` grid returns
`Bm25ParameterTuner`'s own winner and score. That is asserted, not argued:
`AZeroDeltaGridReproducesTheBm25ParameterTunerExactly` runs all three tuners over the same grids
and compares. It is also why `delta = 0` is in the default grid — it is the baseline, and without
it `DeltaHelped` has nothing to measure against and reports `UnflooredMetricScore = NaN`.

### The measurement, and a retraction

| Config | reference (nDCG@5) | NFCorpus (nDCG@10) | SciFact (nDCG@10) |
|---|---|---|---|
| BM25 (default) | 0.8751 | 0.308 | 0.662 |
| BM25 (tuned) | **0.8978** | **0.311** | 0.664 |
| BM25+ (δ=1.0) | 0.8978 | 0.302 | 0.656 |
| BM25+ (tuned) | **0.8978** | **0.311** | 0.666 |
| BM25L (δ=0.5) | 0.8978 | 0.305 | 0.655 |
| BM25L (tuned) | **0.8978** | **0.311** | 0.668 |

Bold marks the best nDCG in each column, not a verdict. **Tuned, the variants win on one corpus out
of three, by 0.002–0.004.** Read the `delta` the harness prints rather than the score column alone:

| Corpus | Tuned BM25 | Tuned BM25+ | Tuned BM25L | `DeltaHelped` |
|---|---|---|---|---|
| reference | 0.8978 | 0.8978 (δ=0) | 0.8978 (δ=0) | false, both |
| NFCorpus | 0.311 | 0.311 (δ=0) | 0.311 (δ=0) | false, both |
| SciFact | 0.664 | 0.666 (δ=0.25) | 0.668 (δ=0.25) | **true, both** |

On NFCorpus all three tuners independently select the *same* parameters — `k1 = 2, b = 0.5,
delta = 0` — and reach 0.311 to the digit, so those three rows are not three results, they are one
result reported three times. That is the degeneracy above showing up at corpus scale: the δ search,
run over 125 points, chooses δ = 0 and hands back BM25.

**On the reference corpus δ is not merely unhelpful, it is inert.** Of the 125 configurations, 46
(BM25+) and 52 (BM25L) tie at the maximum, and among the tied set *every* δ from 0 to 1 appears —
so `δ = 0` is what the deterministic tie-break reached, not a preference. BM25's own search is
unambiguous by comparison: 1 of its 25 points wins. The 22-query reference corpus cannot tell these
formulas apart at all, which is worth knowing before reading its 0.8978 as a verdict on anything.

**SciFact is the only corpus where δ wins, and +0.002 / +0.004 is not a result to quote.** The
harness prints each variant's own unfloored score next to it, and on SciFact that baseline reads
**0.664 — exactly `BM25 tuned`'s score**, which is the degeneracy confirmed at corpus scale: two
independent 125-point searches, both landing on `δ = 0.25` over `k1 = 2, b = 1`. So the margin is
attributable to δ and not to searching more `(k1, b)` points, since both searches cover the same 25
pairs. What it is not is a fair number: 300 queries, binary relevance, fitted in-sample on those
same 300 queries, best-of-125. A 0.004 lead under those conditions is an upper bound. Treat it as
« δ was not obviously harmful here », not as evidence the formula helps.

**ArguAna is the exception, and it is the row this page does not have.** It is also where the
untuned variants do worst anywhere: at a fixed paper δ, BM25+ scores 0.243 and BM25L 0.249 against
BM25's 0.289 — about 15 % relative, the same order as the figure 9bb7c92 retracted, but on a
different corpus and with nothing mis-transcribed. ArguAna was run `--no-tuned`: a 125-point search
over 1406 queries of whole arguments is hours of compute, so **there is no δ-tuned ArguAna row and
the gap's fate under tuning is unmeasured.** Given that SciFact was the only corpus where δ helped
at all, and ArguAna is where it hurts most, this is the corpus where the result is least settled in
either direction. Run it yourself before concluding anything about δ on long-document
counter-argument retrieval.

**What this retracts.** An earlier version of this page reported the variants beating a tuned BM25
by **+0.017** on the reference corpus, and losing by 13–17 % on the BEIR corpora. Both numbers are
gone. The +0.017 was a comparison of BM25+ at a *default* `δ` against BM25 at a *fitted* `(k1, b)`
— 0.8978 against 0.8812 — and the gap was the unfitted `δ`, not the formula. The 13–17 % was
already retracted in 9bb7c92 as a transcription error (both formulas were wrong; fixing them moved
BM25L from 0.575 to 0.655 on SciFact). The +0.017 has now gone the same way: with `δ` searched, it
converges onto tuned BM25 exactly on the reference corpus and on NFCorpus, and is worth +0.004 on
SciFact.

The cost of that earlier claim was a metric mismatch. The 0.8812 it compared against came from a run
whose tuners optimized **F1@k**, which is flat on the reference corpus (0.3588 for all six
configurations) — so the "tuned" BM25 had no signal to fit and the tie-break handed it the first
grid point. The BEIR harness tunes on nDCG; the CLI now takes `--metric` so a tuned row's objective
is stated rather than assumed. Every tuned number on this page is fitted on **nDCG**, the metric it
is reported in.

`DeltaHelped` is the part worth keeping: it is how you find out on *your* corpus whether `δ` is
doing anything, without a table. Two corpora report `false` and one reports `true` by 0.004, all
fitted on their own evaluation queries. The bound is neither a free win nor a loss on these corpora
— which is a real answer, and the one to re-derive on your data rather than inherit.

## BM25F (field-weighted BM25)

`Bm25FScorer` ranks a document that has *fields*. The term frequencies of the fields are summed
with a weight each, and the length normalization is taken over **the fields that contain the term**
— so a term living only in a short title is not penalized for the length of a long body it never
appears in. That is the real difference from running `Bm25Scorer` over the flattened text.

```csharp
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;

var index = new InMemoryTextIndex();
index.Index(new[]
{
    new SearchDocument(
        Id: "1",
        Text: "the body is long and rambles on at some considerable length about many things",
        TextFields: new Dictionary<string, string> { ["title"] = "ranking" }),
});

// A weight per field. A field left out keeps the neutral weight of 1, so an unset map means
// « treat every field equally », not « search the main text only ». A weight of 0 removes the
// field from the ranking entirely.
var engine = new RankedTextSearchEngine(
    index, new Bm25FScorer(fieldWeights: new Dictionary<string, double> { ["title"] = 2.0 }));

engine.Search("ranking");
```

Or from a preset, chaining weights:

```csharp
var scorer = new Bm25FScorer(Bm25FParameters.Balanced.WithWeight("title", 2.0));
```

`Bm25FScorer` implements `IScoreExplainer` (a per-term breakdown whose reported total equals the
score), `ITermOverlapScorer` (a document sharing no query term scores exactly `0`, so the engine
keeps its candidate fast path) and `IQueryPlannableScorer` (a planned search is bit-identical to an
unplanned one — both are asserted in the test suite).

**The formula** is BM25F as given in Robertson, Zaragoza & Taylor, *New formal models of BM25*
(2004), with two departures stated in the source so the behaviour is unambiguous: a single `b` for
all fields where the paper allows one `b_f` per field, and `k1` unrescaled by field count. The
`idf` is the document-level one, the same as `Bm25Scorer`'s.

**Retrieval quality, measured on all three BEIR corpora the harness supports** (nDCG@10; full
tables in [the eval harness README](https://github.com/manuc66/LexiSharp/blob/main/bench/LexiSharp.Eval/README.md)):

| Config | NFCorpus | SciFact | ArguAna |
|---|---|---|---|
| BM25 (k1=1.5, b=0.75) | 0.308 | 0.662 | 0.289 |
| BM25 (tuned in-sample, oracle) | 0.311 | 0.664 | — |
| BM25F (unweighted) | 0.296 | 0.662 | **0.344** |
| BM25F (title 2.0) | 0.296 | **0.665** | **0.344** |
| BM25F (title 4.0) | 0.296 | 0.664 | 0.340 |
| BEIR's published BM25 | 0.325 | 0.665 | 0.315 |

ArguAna was run with `--no-tuned` (its grid search is the dominant cost); NFCorpus 323 judged
queries, SciFact 300, ArguAna 1406. BM25's own row reproduces the numbers this README already
quoted for the same harness, which is the cross-check that the title-field change below left plain
BM25 untouched.

**A correction the numbers forced, and it matters more than the table.** The rows above compare
*default* parameters against each other, and on the reference corpus tuning shows what that was worth:

```
Config                       nDCG@5      MAP@5      MRR@10        R@5
BM25                         0.8751     0.7841     0.9015     0.8409
BM25 (tuned)                 0.8978     0.8144     0.9318     0.8409
BM25F                        0.8189     0.7386     0.8561     0.7955
BM25F (tuned, no weights)    0.8867     0.7955     0.9091     0.8409
```

All tuned rows here are fitted on **nDCG@5**, the metric they are reported in
(`--metric ndcg`).

**Tuned BM25F does not beat a tuned BM25 on the reference corpus — it trails it by 0.011**, and the
un-tuned gap was BM25F's defaults (k1=1.2) being a worse fit for a 42-document corpus than BM25's
(k1=1.5), not a per-field length term doing something useful. This table previously showed the two
tuned rows *equal at 0.8812*, and that equality was an artifact: those runs tuned on **F1@5**, which
is flat on this corpus (0.3588 for every configuration), so the "tuned" rows had no signal to fit
and the tie-break handed both the first grid point. Re-fitted on the metric being reported, BM25F
loses. The conclusion the equality supported — no measured case where BM25F beats BM25 after both
are tuned — survives, and is now better supported than before. The ArguAna 0.344 stands as an
**un-tuned** comparison: whether it survives a tuned BM25 is untested, and running that grid on 1406
queries was not affordable here.

**What the tuner adds is the negative answer, which is the useful one.** `Bm25FParameterTuner` reports
`WeightingHelped = false` on the reference corpus: it searched the title weight and could not beat
leaving it neutral. That is the same conclusion the hand-picked weights reached, arrived at by
search instead of by guessing — and it is the instrument to re-run on your own corpus. The other
standing finding is unchanged: the title weight itself moves nDCG@10 by at most 0.003 across nine
corpus/weight combinations, and on ArguAna the heaviest weight was worse than none.

Full tables are in [the eval harness README](https://github.com/manuc66/LexiSharp/blob/main/bench/LexiSharp.Eval/README.md); reproduce the reference
corpus numbers with `--configs bm25,bm25-tuned,bm25f,bm25f-title,bm25f-tuned --top-k 5`.

The reference corpus moves the same way, and for a boring reason. Indexing its titles (see below)
lifts plain BM25 from 0.8359 to 0.8751 nDCG@5, while BM25F sits at 0.8189 and its title-2.0 variant
at 0.8248 — the weighting helps BM25F by 0.006 there and still does not reach BM25.

What *is* verified about the scorer, rather than inferred: the arithmetic, the term-overlap
contract, the plan parity, the explanation summing back to the score, and a test that a long body
the term never appears in does *not* change the score under BM25F while it does under
`Bm25Scorer`.

## Score boosting (`BoostedTextSearchEngine`)

Wrap any engine to boost or damp its ranking without changing the engine. The boost is a
function of the whole result, so it can read the score, the document metadata or external
data (a closure over your own store):

```csharp
ITextSearchEngine boosted = new BoostedTextSearchEngine(baseEngine,
    result =>
    {
        double factor = 1.0;
        if (result.Document.Category == "priority")
            factor = 2.0;                                  // up-weighted metadata
        if (result.Document.Fields.TryGetValue("stale", out _))
            factor *= 0.5;                                 // damp old matches

        return new ScoreBoost(Multiply: factor, Add: -0.5); // factor and/or offset, signed
    });
```

Writes are forwarded to the inner engine; `Search` applies `score * Multiply + Add` to every
candidate, then re-sorts and re-applies `Limit`/`MinimumScore`. A plain double is accepted as
a multiplicative factor (`result => 2.0`). Positive boosts (factor &gt; 1, positive offset) and
negative ones (factor in (0, 1) damp, negative offset penalty) are equally expressible; factor 0
drops the document entirely. The decorator requests more candidates than the final limit
(`maxCandidates`, default 50) so boosted documents can surface.

## Proximity

BM25 scores a document by *how often* the query terms occur and is blind to *where*. `quick fox` in
« the quick brown fox jumps over the lazy dog » scores exactly what it scores when the two words sit
in the same sentence. `ProximityReranker` is the second stage that notices the difference, using the
term positions the index already stores.

It is a **reranker**, not a scorer, on purpose: it refines a shortlist the first stage already deemed
relevant, and never promotes a document the first stage rejected. The first stage does the recall
work; this only reorders.

The measure is the **minimum window**. With `n` distinct query terms, `W` is the smallest number of
consecutive positions containing an occurrence of each, and `tightness = n / W` — `1.0` when the
terms are contiguous, smaller as they spread. Two shapes turn that into a score, and they are **not**
interchangeable:

```csharp
// Damp: multiply. Can only lower a score, down to the floor at worst.
new ProximityReranker(index, tokenizer, strength: 1.0, mode: ProximityMode.Damp, floor: 0.5)

// Boost: add strength × Σ idf(t) × tightness. The proximity term lives on its own scale,
// so it can only raise a score and never demotes on distance alone.
new ProximityReranker(index, tokenizer, strength: 1.0, mode: ProximityMode.Boost)
```

`strength = 0` is a no-op in both, and so is a `floor` of 1. A single-term query is always a no-op,
and a candidate missing one of the query terms is **left untouched** — whether it should match every
query term is the first-stage scorer's judgement, not this one's. Field boundaries count as distance,
since separate field runs are separated by a position gap.

**The floor is not a knob, it is a bug fix.** The obvious decay `1 - strength × (1 - n/W)` is
unbounded: two terms 81 positions apart in a 100-token document give `n/W ≈ 0.025`, a 97.5 % penalty,
and at opposite ends of a 10 000-token document `n/W ≈ 0.0002`. That punishes a document for being
*long*, when « are these terms near each other? » is a relative question. The first version had no
floor and cost 0.15 nDCG@5 on the reference corpus. The unit tests did not catch it, because they
asserted that an adjacent match outranks a spread one — which holds just as happily under a 2 %
penalty as under a 97 % one.
`TheDampPenaltyIsBoundedRegardlessOfDocumentLength` now pins it.

**And the measurement:**

| Config | reference (nDCG@5) | NFCorpus (nDCG@10) | SciFact (nDCG@10) |
|---|---|---|---|
| BM25 | 0.8751 | 0.308 | 0.662 |
| BM25 (tuned) | 0.8978 | 0.311 | 0.664 |
| proximity, damp s=0.25 | 0.8751 | 0.308 | 0.660 |
| proximity, damp s=1 | 0.8583 | 0.302 | 0.653 |
| proximity, boost s=1 | 0.8751 | 0.308 | 0.662 |

**Damp still loses, but by 0.017 rather than the 0.152 the unbounded version cost** — most of what
that earlier number reported was the bug, not the idea. Boost stays neutral: it moves scores without
moving the ranking, and on NFCorpus it is a hair better on MRR (0.519 vs 0.516) at identical nDCG.

The likely reason neither helps: nDCG@10 here is decided by whether the right document makes the top
ten at all, and BM25's term-frequency signal already orders that set well, so proximity only
reshuffles documents whose scores are already close. The gains it is credited with in the literature
come from exact-phrase tasks and from a tuned phrase *clause* as a separate scoring term, not from
one global strength applied after the fact.

So: **do not assume proximity helps.** It ships because the arithmetic is specified, tested, and
cheap to evaluate on your own corpus — `--configs bm25,bm25-proximity,bm25-proximity-boost`. It is
the right tool when a spread-out match really is a weaker match, which is a property of your data.

## Explainable scoring and BM25 tuning

Audit any ranking decision term by term, then let the corpus pick its own parameters. The
explainers cover the additive scorers — BM25, TF-IDF and query likelihood
(`QueryLikelihoodScorer` also exposes the collection-model contribution of query terms the
document does not contain); a pure filter like `BooleanScorer` has no additive breakdown and
`Explain` returns `null` for it:

```csharp
var engine = new RankedTextSearchEngine(index, new TfIdfScorer());
ScoreExplanation? why = engine.Explain("doc-1", "search engine");
// why.Terms -> per-term TF, IDF and score contribution; why.LengthRatio, why.Parameters...

var bm25 = new RankedTextSearchEngine(index, new Bm25Scorer());
ScoreExplanation? whyBm25 = bm25.Explain("doc-1", "search engine");

var tuner = new Bm25ParameterTuner(index, validationQueries: [
    new Bm25ValidationQuery("search engine", ["doc-1", "doc-7"]),
    new Bm25ValidationQuery("fuzzy matching", ["doc-3"]),
]);
Bm25TuningResult tuning = tuner.Tune(topK: 5);          // grid search over k1 x b
var tunedEngine = new RankedTextSearchEngine(index, new Bm25Scorer(tuning.Parameters));
```

Each validation query lists the relevant document ids; candidates are judged with
`Precision@k`, `Recall@k`, `F1@k` (default) or `nDCG@k` (`RetrievalMetrics`), averaged over the
set. The index is never mutated; `tuning.Grid` exposes every evaluated `(k1, b)` point.

There are three tuners, and picking the right one is the whole point:

| Tuner | Searches | Cost | Reports |
|---|---|---|---|
| `Bm25ParameterTuner` | `k1 × b` | 25 points | `Bm25TuningResult` |
| `Bm25FParameterTuner` | `(k1, b)`, then field weights | 25 + Π weights | `WeightingHelped` |
| `Bm25PlusParameterTuner` | `k1 × b × delta` | 125 points | `DeltaHelped` |
| `Bm25LParameterTuner` | `k1 × b × delta` | 125 points | `DeltaHelped` |

Two properties are shared by all of them and are worth knowing before you read a result. **The
result is an oracle**: the best of N configurations is fitted and scored on the same queries, so it
is an upper bound, and the bound loosens as N grows — 125 points fit more noise than 25. **The
`delta = 0` slice of either variant tuner is a BM25 search**, so a variant's `UnflooredMetricScore`
can be read directly against a `Bm25ParameterTuner` result on the same grids. Score a winner on a
held-out set before believing its margin.
