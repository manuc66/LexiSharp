---
title: Ranking
permalink: pretty
nav_order: 5
---

# Ranking

Ranking is a strategy object: `ITextScorer` reads the index and returns a score, and the
engine does everything else. Swap the scorer, keep the index.

- **`Bm25Scorer`** — Okapi BM25, with ready-made `Bm25Parameters` profiles (`Balanced`,
  `Aggressive`, `Conservative`).
- **`TfIdfScorer`** — TF-IDF.
- **`QueryLikelihoodScorer`** — probabilistic language model (Jelinek-Mercer smoothing).
- **`BooleanScorer`** — exact AND/OR filter.
- **`Bm25PlusScorer` / `Bm25LScorer`** — the published BM25+ (Lv & Zhai 2011) and BM25L
  (Lv et al. 2006) variants, each with a lower bound `delta` on the term frequency.
- **`Bm25FScorer`** — the same `ITextSearchEngine` contract over documents that declare
  `TextFields`: per-field weights, per-field length normalization, an explainable
  per-term breakdown. Requires an index that tracks fields.
- **Score boosting** — `BoostedTextSearchEngine` applies **signed** adjustments
  (multiplicative factor and/or additive offset) per result, without touching the engine
  underneath.

**The measurements are not all flattering, and publishing them is the point.** Each of the
three measured sections below ends with what it actually scored on the corpora tried here:
the BM25 variants beat a tuned BM25 on the reference corpus and lose slightly on NFCorpus
and SciFact; BM25F has **no** measured case of beating a tuned BM25, and its tuner reports
`WeightingHelped = false` on every corpus it has been run on; proximity is neutral at
quarter strength and negative at full strength. On these corpora, fitting parameters is
worth more than adding a scorer — see [Evaluation](evaluation.md).

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

`Bm25PlusScorer` degenerates to `Bm25Scorer` at `delta = 0`, and a test asserts that bit for bit —
but that test **cannot** see where δ sits, since a formula that shifted `tf` by δ inside the fraction
would satisfy it too. `Bm25PlusAddsDeltaOutsideTheFraction` is the test that discriminates, and
`Bm25LUsesTheCompressedDenominatorNotBm25s` does the same job for BM25L against BM25's denominator.
Both exist because the first version of this section got both wrong — see the correction below.

One departure for BM25+: read literally, `idf(t)·δ` applies even to terms the document lacks, so the
paper's model scores non-matching documents above zero. Reference implementations handle that by
computing and subtracting a non-occurrence term (`bm25s` does exactly this). That conflicts with the
« score 0 means no match » convention, so this implementation only sums over terms the document
actually has. BM25L needs no such accommodation — its term weight is already 0 at `tf = 0`.

**And the measurement, which is now a split verdict:**

| Config | reference (nDCG@5) | NFCorpus (nDCG@10) | SciFact (nDCG@10) |
|---|---|---|---|
| BM25 (default) | 0.8751 | 0.308 | 0.662 |
| BM25 (tuned) | 0.8812 | 0.311 | 0.664 |
| **BM25+ (δ=1.0)** | **0.8978** | 0.302 | 0.656 |
| **BM25L (δ=0.5)** | **0.8978** | 0.305 | 0.655 |

**On the reference corpus both variants beat a tuned BM25, by +0.017.** On NFCorpus and SciFact they
lose, but by 0.006–0.009 — not the 13–17 % an earlier, broken transcription appeared to show. BM25L's
previous showing on those two corpora (0.257 / 0.575) was almost entirely the wrong denominator.

The caveat stands: the variants run at a default `δ` while BM25 is tuned, and `Bm25ParameterTuner`
only searches `(k1, b)`. With a δ tuner the BEIR numbers might move, and on the reference corpus the
gain might not survive it either. Re-measure with `--configs bm25,bm25-tuned,bm25+,bm25l`.

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
Config                     nDCG@5      MAP@5      MRR@10        R@5
BM25                       0.8751     0.7841     0.9015     0.8409
BM25 (tuned)               0.8812     0.7917     0.9015     0.8409
BM25F                      0.8189     0.7386     0.8561     0.7955
BM25F (tuned)              0.8812     0.7917     0.9015     0.8409
```

**Tuned BM25F equals tuned BM25 to the digit on the reference corpus**, and the un-tuned gap was
BM25F's defaults (k1=1.2) being a worse fit for a 42-document corpus than BM25's (k1=1.5) — not a
per-field length term doing something useful. So there is no measured case here where BM25F beats
BM25 *after both are tuned*. The ArguAna 0.344 stands as an **un-tuned** comparison: whether it
survives a tuned BM25 is untested, and running that grid on 1406 queries was not affordable here.

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
| BM25 | **0.8751** | **0.308** | **0.662** |
| BM25 (tuned) | 0.8812 | 0.311 | 0.664 |
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
