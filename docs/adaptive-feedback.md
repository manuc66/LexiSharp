---
title: Adaptive feedback
nav_order: 17
description: >-
  Learn from corrections over time and per context, calibrate by recency, decide at a cost, and
  learn a preference re-ranker from logged choices.
---

# Adaptive feedback

`LexiSharp.Adaptive` is the layer above a calibrated distribution. It learns from a stream of
corrections, per context and over time, without retraining the text model on every click.

> **Status: experimental for 1.x.** The surface is additive and may change in a minor release; in
> particular, the preference reranker's context seam is new. Pin a version if you depend on it.

The boundary is deliberate: **LexiSharp provides calibrated distributions and the tools to measure
and decide; the caller owns the temporal structure, the business constraints and the interface.**
Nothing here segments a stream, knows a domain, or generates a question.

## Quick start

```csharp
using LexiSharp.Adaptive;
using LexiSharp.Classification;
using LexiSharp.Core;

var model = new LinearClassifier();
model.Train(documents); // text, categories

var memory = new FeedbackMemory<string>(
    new RecencyPolicy(TimeSpan.FromDays(10), TimeSpan.FromDays(30)));

var ranker = new AdaptiveFeedbackRanker<string>(model, memory);

AdaptiveRanking ranking = ranker.Predict(text, context: sender, now);

// The user accepts, corrects, rejects, ignores, or the system abstains.
ranker.Observe(new FeedbackEvent<string>(
    text,
    ranking.Results[0].Category,
    accepted: null,
    sender,
    now,
    FeedbackOutcome.Rejected));
```

With an empty memory and no calibrator, the ranker returns the model's own distribution, in the
model's order, with the same probabilities. The memory reweights the categories the model already
proposes; it never introduces one.

## Recency and sample weights

`RecencyPolicy` turns an age into a weight in `[0, 1]`:

```csharp
var policy = new RecencyPolicy(
    halfLife: TimeSpan.FromDays(10),
    maxAge: TimeSpan.FromDays(30));

double weight = policy.Weight(now, observedAt, occurrences: 1);
```

The weight halves every half-life; a sample older than `MaxAge` has weight `0` and is dropped.
`occurrences` damps a repeated item: the first has full weight, the eighth (with the default
saturation of 7) has half.

The three text classifiers accept the weight directly:

```csharp
var linear = new LinearClassifier(
    tokenizer: null,
    options: null,
    weightOf: document => policy.Weight(trainingNow, timestamps[document.Id]));

linear.Train(documents);
```

`LinearClassifier`, `DecisionTreeClassifier` and `RandomForestClassifier` have the same
three-argument overloads. For a model that cannot consume a fractional weight, use
`SampleWeightMode.Quantized` and `SampleWeighting.Expand` to produce whole copies; that is an
approximation, not the default.

The delegate is **not** persisted: the trained coefficients already carry the weights. A model
saved and loaded with `ClassificationModelSerializer` predicts the same, without the delegate.

## Feedback memory

`FeedbackMemory<TContext>` keeps two decayed masses per context and category: acceptances and
refusals. It stores aggregates, never text, and the context key is hashed by default.

| Outcome | Effect |
|---|---|
| `Accepted` | credits the accepted category, removes one refusal from it |
| `Corrected` | credits the accepted category, adds one refusal to the offered one |
| `Rejected` | adds one refusal to the offered category |
| `Abstained` | credits the accepted category when one was chosen |
| `Ignored` | counts as an exposure only; **never** a refusal |

`Prior(context, now)` projects the masses to `now` and shrinks the context's acceptance share
towards the global prior. `Vetoed(context, now)` returns the categories whose refusal mass reaches
`FeedbackMemoryOptions.VetoAfter`. A veto removes an offer, not the memory: it decays, and an
acceptance can clear it.

The memory is bounded by `MaxContexts` (least-recently-used eviction), `MaxCategoriesPerContext` and
`MaxPairs`. `Purge(now)` drops contexts older than the policy's maximum age. Reads are pure and
concurrent; `Observe`, `Purge`, `Prune` and `Rebase` take the writer lock.

`AdaptiveOptions.AutoTuneHalfLife` wires a `DriftMonitor` into the ranker: a detected drop halves
the memory and calibration half-life, and a stable stream can grow it again. It is off by default;
the monitor only suggests, the ranker applies the suggestion when the option is on.

## Calibration by recency

`WeightedConformalCalibrator` stores non-conformity scores with their instants and recomputes the
weighted quantile at prediction time. With every weight equal to `1`, it reduces exactly to the
split-conformal index used by `ConformalClassifier`:

```csharp
var calibrator = new WeightedConformalCalibrator(
    alpha: 0.10,
    new RecencyPolicy(TimeSpan.FromDays(10), TimeSpan.FromDays(30)));

var ranker = new AdaptiveFeedbackRanker<string>(model, memory, calibrator: calibrator);
```

Pass the distribution the user actually saw in `FeedbackEvent.Offered`; without it, the ranker
counts a missing score and the calibrator cannot learn from that event. An empty prediction set is
an abstention.

## Decision layer

A calibrated distribution does not say what to do. `CostAwareDecisionPolicy` minimizes an expected
cost and returns one of four actions:

```csharp
var decisions = new CostAwareDecisionPolicy();

Decision decision = decisions.Decide(
    ranking.AllScores.Select(score => new ClassificationResult(score.Category, score.FinalProbability)).ToList(),
    new DecisionCosts
    {
        AskCost = 2,
        AbstainCost = 5,
        Misclassification = new CategoryMisclassificationCost(new Dictionary<string, double>
        {
            ["Urgent"] = 20,
            ["Archive"] = 1,
        }),
    });
```

- `Accept` carries the category with the lowest expected misclassification cost.
- `Ask` carries the smallest candidate set that reaches `AskCandidateMass`, when the value of
  information pays for the question. `ResidualCostAfterAsk` models an imperfect answer.
- `Abstain` is chosen when deferring is cheaper.
- `Explore` is never chosen without an explicit `IExplorationPolicy`; the policy chooses the
  category, and `ExploreCost` is the expected cost of that action.

`Decision.Options` lists all four actions with their costs: that is the explanation.

## Preference re-ranking

When the user chooses the third candidate, that is a relative preference. `PreferenceLearner`
trains one sparse weight vector per category with a pairwise logistic loss (RankNet), AdaGrad and a
seeded shuffle:

```csharp
var learner = new PreferenceLearner();

learner.Train(events); // PreferenceEvent: candidates, chosen, context, propensity

IReadOnlyList<ClassificationResult> reranked = learner.ReRank(
    text,
    contextKey,
    candidates);
```

`ReRank` computes `log p(c) + w_c · φ(text, context)`, then normalizes. Without weights it is the
identity. The default feature source uses text unigrams and one `ctx:<key>` feature; pass an
`IFeatureSource<PreferenceItem>` to add identifiers, repositories or other symbolic features. The
learner never introduces a category.

`PreferenceEvaluator.Replay` measures top-1 agreement, MRR, per-logged-rank slices and a
self-normalized IPS estimate with its effective sample size. IPS needs the logged propensity of the
chosen category; without it, the report says so. DR needs a reward model and is not implemented.

### Search re-ranking

`PreferenceReranker` is the same pairwise idea over a **shared** query–document scorer, so it
generalizes to documents the log never contained (the category learner learns one vector per
category and is the right tool when the label set is closed). It implements `IReranker` and also
exposes the context explicitly:

```csharp
var reranker = new PreferenceReranker(
    resolveDocument: id => _documents.GetValueOrDefault(id));

reranker.Train(clickLog); // PreferenceEvent: candidates are document ids

// The IReranker contract has no context: this overload uses the empty key.
IReranker stage = reranker;
var engine = new RerankedTextSearchEngine(baseEngine, stage);

// A caller that knows the context uses the additive overload.
IReadOnlyList<SearchResult> page = reranker.Rerank(query, contextKey, candidates);
```

The default `RerankFeatureSource` emits query terms (`q:`), document terms (`d:`), the context
(`c:`), the base rank (`r:0`..`r:9`) and **context × document interactions**
(`cx:<context>|d:<token>`). The interaction is what lets the same candidate set be ranked
differently in two contexts; a bare `c:` is constant across candidates and cancels. `Rerank`
returns a new list, never extends it, leaves the input untouched, and is the identity without
weights. The model persists through the same `LXPR` snapshot, with one special `"*"` category
holding the shared vector.

`cx:` features are per context and document token. A high-cardinality context (a query id, a
session, a fresh folder per request) gives each interaction its own feature and overfits quickly;
keep contexts coarse — a family, a repository, a folder — or repeat the context enough for the
interaction to carry evidence. A minimum-support option per interaction is a future extension, not
a default.

## Persistence

`AdaptiveFeedbackRanker.Save(now, modelSignature)` returns an `AdaptiveSnapshot`; write it with
`AdaptiveSnapshotSerializer` (format `LXFA`, version 1):

```csharp
AdaptiveSnapshot snapshot = ranker.Save(now, modelSignature: "classifier-v3");

using (var file = File.Create("adaptive.lxfa"))
    AdaptiveSnapshotSerializer.Save(snapshot, file);

using (var file = File.OpenRead("adaptive.lxfa"))
{
    AdaptiveSnapshot loaded = AdaptiveSnapshotSerializer.Load(file);
    var restored = AdaptiveFeedbackRanker<string>.Load(loaded, model, codec);
}
```

The snapshot carries the options, the memory and the optional calibration, with a trailing SHA-256.
A tampered byte, an unknown version or a codec mismatch is refused. The text model and the context
codec are not in the file, like the tokenizer in `LXCL`: load with the same configuration.

A preference model has its own `LXPR` snapshot:

```csharp
PreferenceSnapshot preference = learner.Save(now);

using (var file = File.Create("preference.lxpr"))
    PreferenceSnapshotSerializer.Save(preference, file);

using (var file = File.OpenRead("preference.lxpr"))
{
    PreferenceSnapshot loaded = PreferenceSnapshotSerializer.Load(file);
    var restored = PreferenceLearner.Load(loaded);
}
```

`LXPR` carries the vocabulary in vocabulary mode, or the hashed indices in hash mode, plus the
canonical preference signature and a trailing SHA-256. The feature source is not stored: load with
the same configuration.

The trailing SHA-256 is an integrity check, not a MAC: it catches corruption, not a malicious file.
Treat a snapshot that crosses a trust boundary as untrusted input; the loader validates the options
before reading counts, applies hard item caps independent of the file, does not preallocate from an
untrusted count, and reports a malformed body as `InvalidDataException`.

A non-hashed preference snapshot contains feature names — document tokens and `cx:<context>|d:<token>`
keys — so a context that is a user or project identifier is stored in clear. Use
`HashFeatures = true` when the snapshot is stored outside the trust boundary; hashed mode trades
explainability for privacy.

## Evaluation

`AdaptiveEvaluator.Replay` predicts before observing, then reports:

- outcome counts and labeled accuracy with a block-bootstrap interval;
- ECE, Brier and log-loss on the human label;
- conformal coverage and mean set size;
- time-to-recovery and repetition regret;
- a self-normalized IPS estimate with its effective sample size.

Labeled accuracy is **conditional on feedback**: it compares the policy with the rows that carried
a human label, not with the unlabeled rows. The report always carries the labeled share beside it.
IPS is a diagnostic, not a deployment gate.

## Benchmark (`adaptive-cost`)

The synthetic benchmark freezes a context-preference drift and replays it through three variants:

```bash
dotnet run --project bench/LexiSharp.Eval -c Release -- \
  adaptive-cost --write bench/LexiSharp.Eval/reference/adaptive-cost.json

dotnet run --project bench/LexiSharp.Eval -c Release -- \
  adaptive-cost --verify bench/LexiSharp.Eval/reference/adaptive-cost.json
```

The scenario trains an ambiguous two-category model, flips the preferred category at event 50, and
logs a user who accepts a correct offer or corrects a wrong one. Default costs are `AskCost = 0.1`
and `AbstainCost = 2`; `--events`, `--drift-at`, `--seed`, `--ask-cost` and `--abstain-cost` change
them.

| Variant | Accepted | Asked | Abstained | Precision | Cost / 100 | Recovered | Mean recovery |
|---|---:|---:|---:|---:|---:|---:|---:|
| `static` | 100 | 0 | 0 | 0.5000 | 50.0000 | 0 | 0 |
| `adaptive` | 100 | 0 | 0 | 0.9900 | 1.0000 | 49 | 1.0000 |
| `decision` | 46 | 54 | 0 | 1.0000 | 5.4000 | 0 | 0 |

`static` and `adaptive` always offer, so their `Accepted` column is the number of offers. The
adaptive row is a hindsight score: it pays `1` for each wrong offer. The decision row pays `0.1` per
question and asks while the underlying distribution is still 50/50; its cost is higher than the
adaptive hindsight cost and far below the static one.

The pin is checked by `AdaptiveCostPinTests` and by `--verify`. This is a mechanism check, not a
production claim: one synthetic corpus, one tokenizer, one scenario.

## Limits

- **No temporal structure.** The library does not segment a stream, detect a rupture or impose a
  duration. That belongs to the caller, above the distributions.
- **Calibration is a precondition.** A cost-aware decision on an uncalibrated distribution optimizes
  the wrong number.
- **No bandit.** The preference learner is supervised by logged choices; exploration is not
  implemented.
- **No DR.** IPS needs propensities; DR needs a reward model for every action.
- **No text in the feedback snapshot.** The memory stores opaque context keys and category labels.
  A preference model in vocabulary mode stores its vocabulary, like `LXCL`.
