---
title: BM25 and ranking functions
nav_order: 5
description: >-
  BM25 and its variants — BM25+, BM25L, BM25F — plus TF-IDF, query likelihood and
  boolean scorers, composing scorers into a weighted sum, score boosting, proximity,
  and per-term score explanations.
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
- **`WeightedCompositeScorer`** — a scorer built out of scorers: the weighted sum of their
  scores in one pass, in one scale. Negative weights make a part a penalty, which is how
  **`DocumentLengthRatioScorer`** (`|D| / avgdl`) becomes a length prior.
- **`WindowBm25Scorer`** — scores a document by its **best window** rather than its whole text,
  over one or more widths. The window is a way of counting term frequencies; the document is
  still the returned unit.

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
additive, and not obviously for BM25L, which is [shown below](#tuning-delta-and-what-it-does-to-the-comparison).
A test asserts the BM25+ equality bit for bit, but it **cannot** see where δ sits, since a formula
that shifted `tf` by δ inside the fraction would satisfy it too. `Bm25PlusAddsDeltaOutsideTheFraction`
is the test that discriminates, and `Bm25LUsesTheCompressedDenominatorNotBm25s` does the same job
for BM25L against BM25's denominator.

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
comparable. BM25+'s is obvious — the bound is additive. BM25L's is not: its denominator carries
the compressed frequency where BM25's carries the raw one, which looks like a difference until you
notice the compression is on the numerator too, so it cancels:

```
ctd / (k1 + ctd) = (tf / norm) / (k1 + tf / norm) = tf / (k1·norm + tf)      ... BM25's term weight
```

So `delta = 0` is plain BM25, term for term, and a `delta = 0` grid returns
`Bm25ParameterTuner`'s own winner and score. That is asserted, not argued:
`AZeroDeltaGridReproducesTheBm25ParameterTunerExactly` runs all three tuners over the same grids
and compares. It is also why `delta = 0` is in the default grid — it is the baseline, and without
it `DeltaHelped` has nothing to measure against and reports `UnflooredMetricScore = NaN`.

### The measurement

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
untuned variants do worst anywhere: at a fixed paper δ, BM25+ scores 0.269 and BM25L 0.276 against
BM25's 0.320 — about 15 % relative, the same order as the figure 9bb7c92 retracted, but on a
different corpus and with nothing mis-transcribed. ArguAna was run `--no-tuned`: a 125-point search
over 1406 queries of whole arguments is hours of compute, so **there is no δ-tuned ArguAna row and
the gap's fate under tuning is unmeasured.** Given that SciFact was the only corpus where δ helped
at all, and ArguAna is where it hurts most, this is the corpus where the result is least settled in
either direction. Run it yourself before concluding anything about δ on long-document
counter-argument retrieval.

**Reading the tables against an unfitted number.** A +0.017 here comes from comparing BM25+ at a
*default* `δ` against BM25 at a *fitted* `(k1, b)` — 0.8978 against 0.8812. The gap is the unfitted
`δ`, not the formula: with `δ` searched, the variants converge onto tuned BM25 exactly on the reference
corpus and on NFCorpus, and are worth +0.004 on SciFact. The 0.8812 it compares against comes from a run
whose tuners optimized **F1@k**, which is flat on the reference corpus (0.3588 for all six
configurations) — so the "tuned" BM25 has no signal to fit and the tie-break hands it the first grid
point. The BEIR harness tunes on nDCG; the CLI takes `--metric` so a tuned row's objective is stated
rather than assumed. Every tuned number on this page is fitted on **nDCG**, the metric it is reported
in.

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
| BM25 (k1=1.5, b=0.75) | 0.308 | 0.662 | 0.320 |
| BM25 (tuned in-sample, oracle) | 0.311 | 0.664 | — |
| BM25F (unweighted) | 0.296 | 0.662 | **0.379** |
| BM25F (title 2.0) | 0.296 | **0.665** | **0.379** |
| BM25F (title 4.0) | 0.296 | 0.664 | 0.375 |
| BEIR's published BM25 | 0.325 | 0.665 | 0.315 |

ArguAna was run with `--no-tuned` (its grid search is the dominant cost); NFCorpus 323 judged
queries, SciFact 300, ArguAna 1406. BM25's own row reproduces the numbers this README already
quoted for the same harness, which is the cross-check that the title-field change below left plain
BM25 untouched.

**What tuning shows the rows above were worth.** The rows compare *default* parameters against each
other, and on the reference corpus tuning prices those defaults:

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
(k1=1.5), not a per-field length term doing something useful. Read those two tuned rows as fitted on
nDCG@5: fitting on **F1@5**, which is flat on this corpus (0.3588 for every configuration), leaves the
tuner no signal to fit and hands both the first grid point, which puts them equal at 0.8812 and hides
the loss. So the comparison stands on the nDCG-fitted rows: no measured case where BM25F beats BM25
after both are tuned. The ArguAna 0.379 stands as an
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

## Composing scorers (`WeightedCompositeScorer`)

A scorer can be built out of scorers: the weighted sum of their scores, `Σ wᵢ · componentᵢ`,
computed in one pass over one candidate set.

```csharp
var scorer = new WeightedCompositeScorer(
    (new Bm25Scorer(),                     1.0),
    (new DocumentLengthRatioScorer(),     -0.2));   // a length prior: long documents lose
```

**Not the same tool as the weighted merger.** `WeightedScoreResultMerger` blends scores *across*
finished rankings and normalizes each list by its own maximum first. That is right for fusing
sources whose scores are not commensurable — a `ts_rank` and a cosine similarity — and wrong for
composing one score out of parts of itself, because a per-list maximum makes the blend depend on how
many documents each list happened to return. Here the weights are the only knob and every part is
in one scale.

**Weights may be negative, and half of what a composite is for depends on it.** A negative weight
is how a part becomes a penalty. The weighted merger refuses negative weights because it blends
non-negative normalized similarities, where a negative weight has no reading; both types say so, and
only non-finite weights are rejected here.

**A zero weight removes the part**, and the part is not called at all — not to save the call, but
because `0 × NaN` is `NaN` and a part that answers with a non-finite score must not be able to
poison a weight saying it does not participate. A composite whose weights are all zero matches
nothing.

**A non-finite part score propagates**, and the engine rejects the document. Dropping the part
instead would produce a score computed from a subset of the components — a plausible-looking wrong
number, harder to notice than a document that is simply missing.

### The length prior, and how it differs from `b`

`DocumentLengthRatioScorer` returns `|D| / avgdl`: one for an average document, above one for a
long one. Weighted negatively it is a length prior. Two properties make it a component rather than a
ranker:

- **It is not a scorer alone.** It reads no query term and matches nothing; used by itself it ranks
  documents by length. Give it a negative weight inside a score that does the matching.
- **Its unit is always the document.** Both figures are the index's `DocumentLength` and
  `AverageDocumentLength`, so the ratio means the same thing in every configuration reading the same
  index — which is what makes it usable as a fixed term across an A/B. An index built over windows is
  a different index with its own average length, and a prior measured against it answers a different
  question than the same prior measured against whole documents.

BM25's `b` lengthens the saturation factor for a long document, which damps a query term *in a
document that matched*, in proportion to how much that term occurred. This term is a property of the
document alone: it is added to every document's score, matching or not, and it does not scale with
any term. Two documents that matched identically can therefore be ranked differently by it, which is
what a prior does and what a normalization does not.

### What the engine cannot do for a composite

A prior part scores a document sharing **no** query term, because a length is a property of the
document whatever the query asked for. Such a document gets a non-zero total and **can reach the
page**. The composite therefore does not claim `ITermOverlapScorer` — the engine cannot skip
non-matching candidates on its behalf, and scans the corpus when nothing else narrows the set. Two
ways to keep the result set clean, both the caller's call: keep a matching part's weight above the
priors', or compose inside a cascade stage so the candidates arrive from a retrieval stage that
already decided what matched.

No quality or latency figure is quoted for either type: neither has been measured on a corpus here.

## Scoring a document by its best window (`WindowBm25Scorer`)

A window here is a way of counting term frequencies, not a separate indexed unit: the document is
returned whole, keeps its id and is ranked against other documents. Nothing here chunks anything,
and the caller need not have chunked anything.

For a window `w` of width `s`,

```
score(w) = Σ_t idf(t) · tf_w(t) · (k1 + 1) / (tf_w(t) + k1)
```

and the document's score is the largest such value over every window of every requested width. There
is **no length normalization** in that formula, and the omission is the point: a window's length is
its own width by construction, so a term's damping inside it is decided by how many times the term
occurs in those `s` positions and by nothing else. Score a document by its best passage and length
stops being what ranks it — if you want length to count, put it back as a term of its own, with a
weight you chose.

```csharp
var scorer = new WindowBm25Scorer([32, 128], stride: 16, k1: 1.2);
```

**The whole document is the same scorer.** `includeWholeDocument: true` scores the document as one
window, where every `tf_w` is the document's term frequency and the normalization is 1 — which is
BM25 with `b = 0`, exactly. `WindowBm25ScorerTests` asserts the equality to the bit over a hand-built
corpus and `WindowBm25ScorerPropertiesTests` over five hundred generated ones, so a
windowed-versus-whole comparison made through this type compares one scorer against itself and the
difference is the windowing and nothing else.

**Combined with widths, that flag decides nothing.** Not approximately: the score is the whole
document's, exactly, and the widths are inert. The cause is the formula above rather than the sweep.
A term's contribution is `tf·(k1+1)/(tf + k1)`, which rises with `tf` for every `k1 > 0`; the whole
document holds at least as many occurrences of every query term as any window of it does; so it
dominates each window term by term, and a maximum over a set that contains it can only be itself. At
`k1 = 0` every term contributes its idf once whatever `tf` is, and the whole document still holds
every term present anywhere in it, so the tie is exact there too. Measured on this type over 78,800
(query, document) pairs at `k1` = 0, 0.4, 1.5, 2.4 and 13.0, the gap between
`new WindowBm25Scorer([w], stride, includeWholeDocument: true)` and
`new WindowBm25Scorer(includeWholeDocument: true)` is zero, while the same scorer with the flag off
differs by up to 394. `WindowBm25ScorerTests` asserts it.

So there is no multi-scale sweep to be had from this type, and a caller who wants one — a maximum over
windows *and* the whole document, which is the shape a sliding-window scan usually wants — does not
get it. The same arithmetic constrains a fusion built on a span contract: for any component monotone
in term frequency, a span set containing the whole document makes the maximum the global score, so a
document's score is its global one and the locality half of the sum computes nothing. A global
component belongs in the weighted sum as a term of its own, outside the maximum.

**What it costs.** One merged pass over the positions that actually match, then one window sweep per
width, per candidate. It never takes the term-at-a-time accumulation pass — that pass scores by
ordinal from a per-term weight and carries no positions, and counting frequencies per window needs
the document in hand — so an engine serving it walks the candidate set document by document. That is
a price, not a defect, and it is the reason the topology this scorer belongs to is a cascade:
retrieval over a shortlist, then this on the shortlist.

**Still a term-overlap scorer.** A document sharing no query term has no position inside any window,
so the score is `0` and the engine may skip it. Composed inside a
`WeightedCompositeScorer` next to a component that scores non-matching documents, that promise
belongs to the composite instead — see above.

**Measured, and not reproducible from this repository.** The harness that produced the figures below
lives outside it, so nothing here can be re-derived by a command in this tree; that is stated once and
not repeated per number. What follows is what it should be read as: an indication of the shape of the
effect on two corpora, not a figure to quote.

*Protocol.* Two corpora of long documents, 400 queries each, tokenized as lowercase word tokens so
that the harness and the type agree on segmentation. The dev/test split is the parity of the sampled
query order, and **the width is chosen on dev and read on test** — reading the best row's test column
is selection over the set the interval is computed on, and it inflates the delta by 0.005 to 0.011.
Intervals are 95 % bootstrap over queries.

| corpus | documents | mean terms/document | width chosen on dev | Δ nDCG@10 | 95 % |
|---|---|---|---|---|---|
| `qmsum` (meeting transcripts) | 197 | 9,271 | 1 024 at stride 0 | **+0.1414** | [+0.0920, +0.1887] |
| `qmsum` | 197 | 9 271 | 512 at stride w/4 | +0.1507 | [+0.1005, +0.1978] |
| `narrativeqa` (book-length narrative) | 355 | 52 862 | 2 048 at stride 0 | +0.0030 | [−0.0193, +0.0257] |
| `narrativeqa` | 355 | 52 862 | 2 048 at stride w/4 | +0.0101 | [−0.0095, +0.0303] |

Those are this type against its own whole-document counterpart — `includeWholeDocument` at `b = 0` —
which is the honest within-family comparison, and **on the second corpus it is nothing**: no width
excluded zero, and the development split's deltas were positive at every width while the test split's
were negative or zero at every width. The same code, the same defaults, the same protocol. A width
cannot tell you which corpus you have, which is why the sweep is the caller's to run.

**The baseline decides which number you are reading.** `b = 0` is a weak scorer: whole-document BM25 at
`b = 0` scores 0.7184 on `qmsum`, where a whole-document BM25 tuned on the same dev split
(`k1 = 1.6, b = 1.0`, an interior optimum) scores 0.8321. Measured against *that* baseline — a
re-implementation of the same formula at the type's conventions, `k1` selected independently per side —
the gain is:

| corpus | Δ nDCG@10 against the tuned baseline | 95 % |
|---|---|---|
| `qmsum` at 512 | +0.0348 | [+0.0032, +0.0667] |
| `narrativeqa` at 512 | **−0.0363** | [−0.0655, −0.0087] |

Those two rows hold one arm's `k1` fixed and tune the other's, which is the asymmetry that produced
them: the windowed arm ran at an effective `k1` of 0.3 to 0.5 against the whole document's 1.2, so some
of what they report is a retuned saturation constant rather than locality. Tuning both arms over a
15 × 7 `(k1, b)` grid on the same development split, at the type's conventions throughout, gives:

| corpus | whole-document `(k1, b)` chosen on dev | windowed `(k1, width, stride)` chosen on dev | Δ nDCG@10 | 95 % |
|---|---|---|---|---|
| `qmsum` | `k1 = 2.4, b = 0.75` | `k1 = 0.4, w = 512, stride w/4` | **+0.0413** | [+0.0060, +0.0794] |
| `narrativeqa` | `k1 = 13.0, b = 0.9` | `k1 = 2.4, w = 2048, stride w/4` | **−0.0198** | [−0.0409, −0.0007] |

Both whole-document optima are interior — `qmsum` peaks at `k1 = 2.4` and falls away on both sides at
every `b` tried, `narrativeqa` at `k1 = 13.0` — so the negative is not an under-tuned baseline. Note
the distance between the two arms' chosen `k1`: a window with no length term saturates earlier than a
whole text does, and that difference is large enough to reverse a sign, which is why holding one `k1`
across both arms measures it instead of locality.

**Neither row is a property of windowing, and the grid says so.** Over the 180 windowed
configurations swept against the tuned baseline, read on test:

| | median Δ | cells positive | dev-selected Δ | best cell on test |
|---|---|---|---|---|
| `qmsum` | **−0.0028** | 48 % of 180 | +0.0413 | +0.0489 |
| `narrativeqa` | **−0.0466** | **0 % of 180** | −0.0198 | −0.0198 |

On `qmsum` the cell the development split picked is very nearly the best cell in the grid, and the
grid's median is a small negative number: one configuration beat a tuned baseline and its
neighbourhood did not. On `narrativeqa` not one of the 180 configurations is positive, the
dev-selected one *is* the best of them, and the width profile improves monotonically out to 2 048 and
is still negative. Read the corpus that pays as a configuration that measured well on that corpus.

**Which queries it pays on.** Read against the same tuned baselines inside strata, the delta tracks
query length on both corpora, and does not track document length consistently:

| | `qmsum` | `narrativeqa` |
|---|---|---|
| shortest tercile of distinct query terms | +0.061 [+0.001, +0.125] | +0.020 [−0.007, +0.049] |
| longest tercile | −0.030 [−0.084, +0.023] | −0.030 [−0.071, +0.005] |
| corr(Δ, query length) | −0.72 | −0.75 |
| corr(Δ, judged-document length) | −0.84 | **+0.30** |

The document-length correlation changes sign between the two corpora, which is not what a moderator
looks like; on `qmsum` the short documents carry the short questions, so its shortest-quartile result is
a query-length result. Of the eight cells across both corpora, one — `qmsum`'s shortest quartile of
judged-document length, **+0.130 [+0.060, +0.205]** — has an interval excluding zero. A window holding
512 tokens cannot co-locate a 119-term query, and these are the two facts on either side of it.

So a caller who has already tuned `Bm25Scorer` should expect far less than +0.141, and on
book-length narrative should expect a loss. That is the figure that answers "should I adopt this
instead of tuning what I have", and it is the one to plan against.

**The stride is half the decision.** The type's default makes windows abut; a stride of a quarter width
overlaps them four deep, which is worth +0.151 instead of +0.141 at a narrower width. It also decides
whether a narrow window is unusable or merely useless: at 16 terms against a stride of a quarter width
the delta is +0.005 [−0.045, +0.053], and at the default stride −0.022 [−0.070, +0.025]. Neither is
the −0.20 an earlier re-implementation reported at that width, which was its fixed-count candidate
generator and not the width.

**The identity was checked here, at this document length.** `includeWholeDocument` and
`Bm25Scorer(k1, b: 0)` agreed to the bit — maximum absolute difference 0 — over all 78,800
(query, document) pairs on both corpora, and a Python re-implementation of the same formula reproduced
every score to 4 × 10⁻¹⁴ at the stride that dump was written at.

That figure was previously given as 1.7 × 10⁻⁶, read against a dump taken at a stride of a quarter
width — which is not the stride the dump holds. At `w/4` the same arithmetic diverges from it by up
to 30, the size of a score rather than of a rounding error, and the windows that do match are those
whose start happens to be a multiple of the width. The 1.7 × 10⁻⁶ was a `float32` prefix-sum floor
that did not apply: prefix counts are exact integers below 2²⁴, so the only difference left is the
order of the double summations. A second dump, written explicitly at `w/4`, agrees to 9 × 10⁻¹⁴ over
all 78,800 pairs, and each dump now carries its stride in its filename. This mattered because the
`stride w/4` result had never been cross-checked against the type at that stride. The generated tests
establish the identity; these establish it where a window is a decision rather than a formality.

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

## Learning from past choices (`QueryFeedbackHistory`)

A ranking function reads the query and the corpus. Sometimes the answer depends on something the
text does not carry — an internal name for a recurring obligation, an abbreviation only the people
who use it know. The evidence for it is what was chosen last time, and that evidence is already
in application logs.

`QueryFeedbackHistory` holds which document each query resolved to, and `FeedbackAwareTextSearchEngine`
consults it on every search:

```csharp
using LexiSharp.Ranking;

var history = new QueryFeedbackHistory();
var engine   = new FeedbackAwareTextSearchEngine(baseEngine, history, maxBoost: 5.0);

// On every query the user answers, record the choice.
engine.Learn(userQuery, selectedDocumentId);

var results = engine.Search(userQuery);
```

Two ways to read the history: `GetAssociations` is the exact lookup for a repeated query, and
`GetFuzzyAssociations` scores every recorded query by term overlap — intersection over the longer
of the two term sets — so a differently-worded query still reaches the document it led to before.
Recorded documents carry a strength from how often they were chosen under that query; a query
sharing no terms with the recorded one contributes nothing.

The engine adds that strength as an additive bonus to the primary score, bounded by `maxBoost`,
then re-sorts. The bonus lands after the base ranking, so it promotes among what the primary
ranking already returned and never introduces a document it rejected. That is what lets it
compose with a boosted or hybrid engine rather than replace one.

`Snapshot` and `Restore` persist the history with its counters intact — a query answered five
times is stored as five, not as one.

## Boosting on the search, not only on the document

`BoostedTextSearchEngine`'s boost sees one candidate. That is enough for a decision the document
settles — its field weights, its category, its age — and not enough for one the *ranking* settles,
which is the decision that matters when a boost is a correction rather than a preference.

`BoostedTextSearchEngine<TPayload>` widens what the boost is told. It receives a
`BoostContext<TPayload>` carrying the query as issued, the inner engine's own pre-boost ranking,
the confidence of each candidate, and the caller's payload:

```csharp
using LexiSharp.Core;

// Fires only when the base ranking is close. TopConfidence is the WinnerMargin gap to the
// runner-up: near 1 is a clear winner, near 0 is a near-tie.
var engine = new BoostedTextSearchEngine<CallerContext>(
    inner,
    (context, candidate) =>
        context.TopConfidence < 0.2 && candidate.DocumentId == "b"
            ? new ScoreBoost(Add: 50)
            : ScoreBoost.None());

var results = engine.Search(query, options, new CallerContext(user, DateTimeOffset.UtcNow));
```

The context is built once per search and shared by every candidate, so a check on the whole ranking
is evaluated once per query rather than once per document. `ScoreBoost.None()` is what a boost
returns when it declines — **not** `default(ScoreBoost)`, which is all zeroes and therefore
*excludes* the document. That distinction is called out on `ScoreBoost` because the two read alike
and mean opposites.

`BoostContext<TPayload>` is a class, not a `readonly record struct`. It is handed to the boost once
per candidate, so a struct would be copied on each of those calls — and a copy cannot hold the
memoized confidence array, which would then be rebuilt per candidate instead of per query. At four
reference fields the struct form is also not free: 32 bytes copied per candidate call against 48
bytes of allocation for one instance per query.

The confidences are computed on first read rather than at construction, so a boost that reads only
the payload never pays for the walk. On a 500-document corpus at `maxCandidates: 50`: the
candidate-only `BoostedTextSearchEngine` allocates 8928 B/search, a contextual boost reading only
`Payload` the same 8928 B, and one reading `TopConfidence` 9776 B.

The payload-less overload passes `null`, and the boost sees
`context.PayloadSupplied == false`. A caller holding a plain `ITextSearchEngine` cannot reach the
payload at all, so a boost that is *built* on it stops applying without any sign — the results
still look plausible. For a boost that cannot work without one:

```csharp
var engine = new BoostedTextSearchEngine<CallerContext>(
    inner,
    (context, candidate) => /* ... */,
    requirePayload: true);
```

The payload-less `Search` then throws, naming the engine and the interface it cannot reach. It
throws after the empty-request check and before the inner engine runs, so a request that could
never produce results still returns an empty page and a misrouted call costs nothing. Leave the
flag off for a boost that reads only the ranking, which needs no payload and must keep working
through the plain `ITextSearchEngine` surface.

`BoostContext.TopScore` is a raw score on the inner engine's scale — BM25, a dense similarity, a
fusion score. A `Ranking.CalibratedScoreConfidence` fitted on one of those says nothing about
another, so a gate comparing it against a fitted threshold has to re-fit when the pipeline's
engine changes. `TopConfidence` is the scale-invariant alternative, relative to the result set
rather than a probability. `docs/observability.md` has the two composed.

### Where the payload goes, and why it is not in `SearchOptions`

`TPayload` is the caller's per-search state, reaching the boost as `context.Payload`. It is
deliberately not a property of `SearchOptions`: that record has value equality and a serializable
shape, and a caller-defined value among its properties would put both at the mercy of whatever
`Equals` that type implements. The payload travels as a separate argument instead.

When a boost needs more than one concept — user *and* date, say — that is a `record` the caller
owns, not a shape this library has to guess at:

```csharp
internal sealed record CallerContext(string User, DateTimeOffset Now, string? ExperimentArm);
```

### Finding out whether an engine accepts one

The generic engine implements `IContextualSearchEngine<TPayload>`, an optional capability in the
same family as `IFacetedSearchEngine` or `IDetailedSearchEngine`. Code holding a plain
`ITextSearchEngine` cannot tell from the type whether a payload will be honoured, so it tests:

```csharp
var results = engine is IContextualSearchEngine<CallerContext> contextual
    ? contextual.Search(query, options, userContext)
    : engine.Search(query, options);
```

The payload type is the interface's type parameter rather than `object`, so a
`BoostedTextSearchEngine<CallerContext>` answers to `IContextualSearchEngine<CallerContext>` and
nothing else: handing it the wrong state does not compile. The plain
`Search(query, options)` overload still exists and passes `default`, which is what lets an unaware
pipeline run the engine at all — without the state it had the means to use.

`BoostedTextSearchEngine` (no type parameter) is the original, candidate-only form and is
unchanged. It forwards to the generic one, so there is one ranking implementation.

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
`TheDampPenaltyIsBoundedRegardlessOfDocumentLength` pins it.

**And the measurement:**

| Config | reference (nDCG@5) | NFCorpus (nDCG@10) | SciFact (nDCG@10) |
|---|---|---|---|
| BM25 | 0.8751 | 0.308 | 0.662 |
| BM25 (tuned) | 0.8978 | 0.311 | 0.664 |
| proximity, damp s=0.25 | 0.8751 | 0.308 | 0.660 |
| proximity, damp s=1 | 0.8583 | 0.302 | 0.653 |
| proximity, boost s=1 | 0.8751 | 0.308 | 0.662 |

**Damp loses, by 0.017 — against 0.152 for the unbounded decay this replaced**, so most of what that
larger penalty measured was the bug rather than the idea. Boost stays neutral: it moves scores without
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
