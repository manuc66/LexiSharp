---
title: Classification and text analysis
nav_order: 8
description: >-
  Lexical similarity, keyword extraction and Naive Bayes classification, in the
  same dependency-free core package.
---

# Classification and text analysis

Three things you often need next to a search box, none of which is a ranking function:
near-duplicate detection, keyword extraction, and classification. All of them are in the
core package and none of them needs an index — except where a corpus makes the result
better.

- **Lexical similarity** (`LexiSharp.Similarity`) — pairwise token-set measures (Jaccard,
  Sørensen–Dice) over the library tokenizer, a `pg_trgm`-style character trigram similarity,
  and a rolling Levenshtein edit distance.
- **Keyword extraction** (`LexiSharp.Keywords`) — a corpus-backed **TF-IDF** extractor
  (demotes corpus-frequent words) and a graph-based **TextRank** extractor (weighted
  co-occurrence graph + PageRank), both deterministic and tokenizer-configurable.
- **Classification** (`LexiSharp.Classification`) — multinomial Naive Bayes with Laplace
  smoothing, or Complement Naive Bayes for imbalanced labels, behind a dedicated
  `ITextClassifier` interface, with incremental `Learn`/`Unlearn` for labelled documents that
  arrive over time. A nearest-centroid classifier reads vectors from an `IEmbeddingProvider`
  (E5, MiniLM, an HTTP endpoint — LexiSharp never runs the model), a nearest-neighbour classifier
  ranks terms with BM25 or vectors with cosine, and `FusedTextClassifier`/`BlendTextClassifier`
  combine the posteriors — two lanes, or any number with candidate and prior evidence.

## Lexical similarity and keyword extraction

Pairwise similarity for near-duplicate detection and record de-duplication — token-set
measures over the library tokenizer, `pg_trgm`-style trigrams, and Levenshtein:

```csharp
using LexiSharp.Similarity;

bool duplicate = LexicalSimilarity.Jaccard(stored, incoming) > 0.5;
double fuzzy   = LexicalSimilarity.Trigram("kubernetes cluster", "kubernetes clusters");
int edits      = LevenshteinDistance.Distance("kitten", "sitting"); // 3
```

Keyword extraction pulls the representative terms out of a text. TF-IDF becomes corpus-aware
when built over an `ITextIndex`; TextRank needs no corpus at all:

```csharp
using LexiSharp.Keywords;

IKeywordExtractor tags = new TfIdfKeywordExtractor(someIndex, StopWordTokenizer);
IKeywordExtractor graph = new TextRankKeywordExtractor(StopWordTokenizer); // co-occurrence + PageRank

foreach (var keyword in graph.Extract(document.Text, topN: 5))
    Console.WriteLine($"{keyword.Term}: {keyword.Score:F3}");
```

## Classification

```csharp
using LexiSharp.Classification;

var classifier = new NaiveBayesClassifier();
classifier.Train(trainingDocuments); // requires a non-null SearchDocument.Category

foreach (var prediction in classifier.Predict("i cannot connect to the internet"))
    Console.WriteLine($"{prediction.Category}: {prediction.Probability:P}");
```

The classifier is a `IWeightedPredictor` too (`classifier is IWeightedPredictor`): a
spell-corrected token can carry less evidence than an exact match by passing
`WeightedToken`s directly. `Predict`/`PredictBest` accept a set of `excludedCategories` to
hide hot categories at runtime without retraining (probabilities renormalize over the rest).

### Declining to answer

`Predict` always returns a distribution — the model has a prior whether or not it has seen the
input. Text that shares no token with the training vocabulary is therefore still classified, and
the number it comes back with is not a probability of anything.

Under Laplace smoothing an unseen token contributes `log(alpha / (N_c + alpha*V))` to class `c`,
where `N_c` is that class's token count and `V` the shared vocabulary. The class with **less**
training text has the smaller denominator and so gains the most evidence: a paragraph of words the
model has never seen reads as evidence for whichever class was trained on the least, and the
confidence *rises* with the amount of text the model cannot interpret. On a 7-document/3-document
split, French prose sharing no token returns the minority class at `0.97`, and nonsense at `0.88`.

The case with no tokens at all — an empty string, whitespace — is different and quieter. The
likelihood contributes nothing, so the answer is exactly the class prior: `0.70` on that split.
Correct arithmetic, and still not an answer to "what is this text".

```csharp
var strict = new NaiveBayesClassifier(
    options: new NaiveBayesOptions { AbstainWithoutVocabularyOverlap = true });

var results = strict.Predict(frenchProse);   // empty — no shared token to answer from
var best   = strict.PredictBest(frenchProse); // null
```

The option is off by default, so no existing caller's numbers move. It declines to report the
scores above rather than correcting them; `SkipOutOfVocabularyTokens` is the separate knob that
stops unseen tokens contributing evidence in the first place, and it does not abstain either —
an input with nothing left to contribute still falls through to the prior.

To decide *without* paying for a prediction, or to fall back to something other than nothing:

```csharp
if (classifier.HasAnyVocabularyOverlap(input))
    return classifier.PredictBest(input) is { } category ? category : fallback;
return fallback;   // unseen text: say so, do not guess from the prior
```

`HasAnyVocabularyOverlap` tokenizes with the model's own tokenizer, so it agrees with `Predict`,
and a text that tokenizes to nothing reports no overlap — which is the intent. Reinforced terms
count as vocabulary even though the corpus never carried them: `Reinforce` writes to a ledger and
a term the corpus never saw still contributes evidence there at full weight, so abstaining on such
an input would discard feedback the user gave explicitly. An untrained model reports no overlap for
anything, since it has no vocabulary.

### Incremental learning

`NaiveBayesClassifier` is also an `IIncrementalTextClassifier` (`classifier is
IIncrementalTextClassifier`), for labelled documents that arrive over time:

```csharp
// Adding one document. A model built this way is indistinguishable from one handed the whole
// corpus: same vocabulary, same per-class term counts, same document frequencies, same priors.
classifier.Learn(new SearchDocument("9", "package never arrived", Category: "Shipping"));

// Retracting one. Exactly undoes a Learn of the same document, and retires the category once its
// last document is gone — so a model can be built up and taken back down without ever retraining.
classifier.Unlearn(document);
```

Parity with `Train` is the contract, and it is tested under all twelve `NaiveBayesOptions`
combinations rather than only the defaults, because parity that holds only on the defaults is not
parity. Learning the same document twice weighs twice as much, matching two copies in the corpus.

`Unlearn` is arithmetic, not bookkeeping: it removes one document from the category and subtracts
that text's contribution, and it does not check that the text was ever learned. Tracking which
documents went in would cost memory proportional to the corpus, where the model itself is
proportional to the vocabulary. So unlearning something never seen is well-defined rather than an
error — the class loses a document of prior and nothing else, because there is nothing else to
subtract. Callers who mean "retract exactly what I added" should track it themselves; callers who
mean "this category has one document fewer" can just call it.

`Train` must not overlap any `Predict`; concurrent `Predict` calls are safe. `Learn` and `Unlearn`
are mutations and are not safe alongside either.

### What incremental learning costs

Measured on 12 logical processors, 10 000 documents of 30 tokens each over 5 categories, medians
of 7 rounds.

| Operation | Cost |
|---|---|
| `Train` 10 000 documents from cold | 67.9 ms |
| `Learn` the same 10 000 onto a warm model, 1st pass | 49.6 ms (0.730x) |
| `Learn` the same 10 000 onto a warm model, 2nd pass | 48.5 ms (0.713x) |
| one `Learn` | 0.90 µs |

The incremental pass is *cheaper* than the batch it replaces, which is the point: `Learn` does not
build a vocabulary set alongside the counts the way `Train` does, it derives the vocabulary size
from the counts already there. One `Learn` is ~75 000x cheaper than one retrain on this corpus,
which is the entire argument for having it.
## What the incremental and reinforced members cost

Measured on 12 logical processors, 10 000 documents of 30 tokens each over 5 categories, medians of
7 rounds.

| Operation | Cost |
|---|---|
| `Train` 10 000 documents from cold | 67.9 ms |
| `Learn` the same 10 000 onto a warm model, 1st pass | 49.6 ms (0.730x) |
| `Learn` the same 10 000 onto a warm model, 2nd pass | 48.5 ms (0.713x) |
| one `Learn` | 0.90 µs |

The incremental pass is *cheaper* than the batch it replaces, which is the point: `Learn` does not
build a vocabulary set alongside the counts the way `Train` does, it derives the vocabulary size
from the counts already there. One `Learn` is ~75 000x cheaper than one retrain on this corpus,
which is the entire argument for having it.

Concurrency, 2 s windows with 8 readers and 2 writers (one on the reinforcement ledger, one calling
`Learn`/`Unlearn`):

| | Reads | Failures |
|---|---|---|
| unwrapped, run 1 | 5 188 591 | 8 (0.00 %) |
| unwrapped, run 2 | 6 826 690 | 6 659 (0.10 %) |
| unwrapped, run 3 | 6 657 330 | 667 (0.01 %) |
| wrapped, 3 runs | ~2.6 M | 0 |

The bare failure counts are erratic because a race only sometimes lands, and they will not
reproduce on other hardware; the qualitative result will. Read-path cost of the lock came out at
0.986x / 1.051x / 1.057x / 0.975x / 1.043x across five runs — straddling 1.0, so the uncontended
read lock is not measurably expensive here, though it is not free either. Those figures predate the
async-capable gate that replaced `ReaderWriterLockSlim`; the new gate has not been re-measured, so
read them as an order of magnitude.

### Prediction and mutation at the same time

The models keep their single-writer contract; the synchronization is opt-in, so a single-threaded
caller pays nothing. Wrap the model once, at the point where requests and feedback can overlap, and
send every call through the wrapper:

```csharp
using var safe = new SynchronizedTextClassifier(classifier);

// Request threads:
var best = safe.PredictBest(text);

// Feedback threads:
safe.Learn(new SearchDocument("9", "package never arrived", Category: "Shipping"));
safe.Reinforce(text, category);

// The async surface is covered by the same lock:
var asyncBest = await safe.PredictAsync(text);
await safe.LearnAsync(new SearchDocument("10", "refund requested", Category: "Billing"));
```

The wrapper takes a read lock for `Predict`/`PredictBest`/`PredictAsync`, so readers overlap, and a
write lock for `Train`/`Learn`/`Unlearn`/`Reinforce`/… and their async counterparts. A writer waits
for the predictions already in flight and blocks new ones for the duration of the write. The async
members are available when the wrapped model implements `IAsyncTextClassifier` (the centroid and
nearest-neighbour classifiers do); a synchronous model throws `NotSupportedException` naming the
type instead of silently blocking. `SupportsIncrementalAsync` separately reports whether
`LearnAsync`/`UnlearnAsync` exist, and an async call cancelled while it waits for the lock leaves
the queue instead of waiting behind a long writer.

If a retrain is frequent enough that waiting is a problem, build a fresh model off-lock and publish
it atomically instead:

```csharp
var reference = new AtomicClassifierReference(trained);

// Request thread — capture once per prediction:
var model = reference.Current;
var best = model.PredictBest(text);

// Retrain thread — build first, then swap:
var next = new NaiveBayesClassifier();
next.Train(newCorpus);
var displaced = reference.Swap(next); // do not mutate or dispose it: a reader may still hold it
```

Do not clone the model for every `Learn`: one `Learn` is under a microsecond, while copying the
corpus or the vocabulary is orders of magnitude larger. The snapshot path is for retraining, which
already builds a new model, and it assumes a single writer owns the swaps.

Two things stay outside the wrapper: `ClassificationModelSerializer.Save`, which needs a quiescent
or snapshot model, and the generic models (`LinearClassifier<TItem>`, `CentroidClassifier<TItem>`,
the question trees), which do not implement `ITextClassifier`. Calling those directly on the
wrapped instance bypasses the lock; use a snapshot, or the same external synchronization. Wrap the
model once: two wrappers around the same instance have two independent locks, and wrapping a blend
while also wrapping its lanes nests locks rather than ordering them.

## Centroids over embeddings

Naive Bayes classifies on terms. A **centroid classifier** classifies on vectors: one mean per
category, ranked by cosine similarity, with a softmax over the cosines as the posterior. It shares
no token with the input, so it can recognise a paraphrase the lexical model never saw — and it is
weaker on rare identifiers and exact spellings, which is why the two are available together.

The text wrapper takes an `IEmbeddingProvider`; LexiSharp does not run the model. With the
`LexiSharp.Onnx` package, an E5 export is the intended path:

```csharp
using LexiSharp.Classification;
using LexiSharp.Core;
using LexiSharp.Onnx;

int unknown = new SentencePieceOnnxTokenizer("e5/sentencepiece.bpe.model").UnknownId;

var tokenizer = new SentencePieceOnnxTokenizer("e5/sentencepiece.bpe.model", new()
{
    IdMap = id => id == unknown ? 3 : id + 1, // the Xenova E5 export's own mapping
    PrefixId = 0,
    SuffixId = 2,
});

using var e5 = new OnnxTextEncoder(new OnnxTextEncoderOptions
{
    ModelPath = "e5/model.onnx",
    Tokenizer = tokenizer,
    Pooling = OnnxPooling.Mean,
    Normalize = true,
    QueryPrefix = "query: ",
    PassagePrefix = "passage: ",
});

var classifier = new CentroidClassifier(e5);
classifier.Train(trainingDocuments);          // requires a non-null SearchDocument.Category
var category = classifier.PredictBest("the package never arrived");
```

Centroid classification is **symmetric** — the labelled examples and the text being classified
play the same role — so the default is `EmbeddingUse.Query` on both sides; E5's model card
recommends its query prefix for symmetric tasks. Set `CentroidClassifierOptions.TrainingUse` and
`PredictionUse` to `Passage` when your model expects its passage convention on the examples
instead. Train and predict on the same side: mixing the two embeds the centroids and the input in
different spaces.

The generic `CentroidClassifier<TItem>` takes a vector and a label from the caller, so the same
model classifies a window title, a time bucket or an image descriptor:

```csharp
var generic = new CentroidClassifier<Observation>(
    observation => observation.Vector,
    observation => observation.Category);
```

### Blending it with Naive Bayes

`FusedTextClassifier` trains both models and blends their posteriors:
`(1 - w) · P(lexical) + w · P(centroid)`. `w` is the centroid model's weight and must be tuned on
a validation split; the class blends probabilities, it does not calibrate them.

```csharp
var fused = new FusedTextClassifier(
    new NaiveBayesClassifier(),
    new CentroidClassifier(e5),
    secondWeight: 0.95); // measured on short 20 Newsgroups subjects; tune on your validation split

fused.Train(trainingDocuments);
var category = fused.PredictBest("the package never arrived");
```

The two models need not know the same categories: a category only one of them holds enters the
blend with a zero contribution from the other, and the result is renormalized. Ties are ordered by
category name, so the output is deterministic.

### More than two lanes, and evidence that was never trained

`BlendTextClassifier` takes any number of trained lanes and two optional kinds of evidence:

```csharp
var blend = new BlendTextClassifier(
    new[]
    {
        new TextClassifierLane(nb, 0.8),
        new TextClassifierLane(centroid, 0.2),
    },
    candidates: new[] { new CandidateLane(new JiraKeySource(), 1.0) },
    priors: new[] { new PriorLane(new RecentSubjectSource(), 0.5) });

blend.Train(trainingDocuments);
var category = blend.PredictBest("see PROJ-42, like the ticket next door");
```

A candidate source (`ICandidateTextSource`) may introduce a category no model learned: the text
names `PROJ-42`, so `PROJ-42` enters the blend with its own normalized mass and can win. A prior
source (`ITextPriorSource`) can only shift a category a trained lane already produced; its score
is a **log** multiplier, so two sources compose by adding their bonuses. A prior does not apply to
a category a candidate source injected. The rules are deliberate: evidence
introduces, a prior reweights. Weights are tuned on validation like any other blend weight, and a
probability average is still not a calibration. The Jira regex, the neighbour subjects and the
recency term are the caller's domain and stay outside `LexiSharp.Classification`.

`BlendTextClassifier` is itself an `IIncrementalTextClassifier`: `Learn` and `Unlearn` reach every
lane. The lanes are checked before the first mutation, so a blend that contains a non-incremental
model refuses the call instead of leaving half of its lanes updated. A document that belongs to a
request, not to the configured sources, uses the `BlendContext` overload:

```csharp
var context = new BlendContext
{
    Candidates = new[] { new TextEvidence("PROJ-42", 2.0) },
    Priors = new[] { new TextEvidence("Support", Math.Log(1.5)) },
};

var category = blend.PredictBestWithContext("see PROJ-42", context);
```

The context is read once and never stored, so two calls cannot leak evidence into each other and a
caller does not rebuild the whole blend for every query. Its candidates introduce categories the
same way a configured source does; its priors only shift categories a trained lane produced.

Candidate scores are normalized within each source or context, so the **absolute** amount of
evidence is discarded. Two neighbours that agree do not weigh more than one unless the caller folds
the agreement into `CandidateWeight` — for example by scaling it by the sum of the evidence. A fixed
weight is right when every positive candidate means the same thing; a sum-scaled weight is right
when five agreeing neighbours should outvote one. The choice is a validation decision, and a wrong
one can cost several points.

### What the centroids cost and how they read

Training is one pass over the vectors plus the mean and the centroid norms; prediction is one
dot product per category against those cached norms — no term lookup, no vocabulary. The model
holds `categories × dimension` floats, so an E5-small model over 20 categories is about 31 KB.

Measured on the **subject-only** split of 20 Newsgroups (6,577 train / 3,761 test after the same
deduplication the lexical lane uses), with `multilingual-e5-small` from Xenova's ONNX export in
its int8 form (384 dimensions, 64 tokens, query prefix on both sides), temperature `0.1`, and the
blend weight selected on 987 held-out training documents:

| Method | Accuracy | Macro-F1 |
|---|---:|---:|
| Naive Bayes | 0.5624 | 0.5293 |
| Centroid (E5) | 0.5621 | 0.5413 |
| Fusion (`w = 0.95`) | **0.6240** | **0.5942** |

The lexical baseline reproduces the subject-only figure published for the lexical lane, so the
split is the same one. The interesting part is where the weight lands: `0.95` means the centroid
makes the decision and Naive Bayes breaks its near-ties. The blend is not a 50/50 average — at
`w = 0.75` it reads 0.5807 / 0.5481, at `w = 0.50` 0.5674 / 0.5337 — because the centroid's
softmax at temperature `0.1` is sharp, and a small lexical term is enough to move a
close second place without letting the lexical model's own errors dominate. Reproduce with:

```bash
dotnet run --project bench/LexiSharp.Eval -c Release -- classification-centroid \
  --data /path/to/data --model /path/to/e5-export --text subject --max-seq 64
```

The model is external: the command reads `onnx/model.onnx` (or `model.onnx`) and
`sentencepiece.bpe.model` under `--model`. This is one corpus, one split, one seed and one
temperature; `--text full` runs the long-message configuration, where the lexical model is at its
strongest and the blend's gain is not claimed here.

### Learning a centroid one document at a time

`CentroidClassifier` is an `IIncrementalTextClassifier`. `Learn(document)` adds the document's
vector, its document count and its contribution to the corpus mean; `Unlearn(document)` removes
them and retires a category that loses its last document. The arithmetic is exact with a
deterministic provider, and learning a corpus one document at a time is indistinguishable from
`Train` on the whole corpus — the same parity contract `NaiveBayesClassifier` has. The model is
persisted, sums and global mean included, through `ClassificationModelSerializer`; the embedding
provider is not stored, exactly as the tokenizer is not, so load it with the same provider.

An empty vector means "no embedding": `Train`, `Learn` and `Unlearn` skip that item, and
`Predict` returns nothing for it. It is not a zero vector, so a provider can decline a text without
a sentinel value or a dimension trick.

`CenterOnGlobalMean` subtracts the corpus mean from every centroid and from the query before the
cosine. It can change the ranking when a large shared direction dominates the raw cosine, and it
changes the cosine scale, so the temperature and a fused model's weight have to be tuned again.
It is **off by default and its effect is not measured here**; run `classification-centroid --center`
with your export before turning it on.

What this still does not do: there are no class priors (a frequent category is not favoured), and
there is no calibration between the two models. The temperature changes confidence, never the
cosine ranking.
## Nearest neighbours: BM25 or cosine

`NearestNeighborClassifier` is the non-parametric option: it stores the labelled documents and
lets the closest ones vote. The lexical constructor ranks with the same Okapi BM25 formula as the
search engine; the dense constructor stores one vector per document and ranks by cosine.

```csharp
var lexical = new NearestNeighborClassifier(options: new NearestNeighborOptions
{
    K = 5,
    Vote = NeighborVote.Score,
    Bm25 = Bm25Parameters.Balanced,
});
lexical.Train(documents);

var dense = new NearestNeighborClassifier(e5, new NearestNeighborOptions
{
    K = 10,
    Vote = NeighborVote.Temperature,
    Temperature = 0.1,
});
dense.Train(documents);
```

`Vote` decides what a neighbour contributes: `Score` uses its non-negative similarity, `Rank`
uses `1 / (rank + 1)` and ignores the scale, and `Temperature` applies a softmax to cosine
similarities. The chosen neighbours are aggregated by category into a normalized distribution;
excluded categories are filtered **before** the top-k cut, so an excluded neighbour does not
consume a place. Ties are ordinal. `Learn` adds one document and `Unlearn` removes it by
`SearchDocument.Id`; duplicate ids are rejected, and unlearning an id the model does not hold is a
no-op.

On the dense path an empty vector means "no embedding": training and `Learn` skip the document and
`Predict` returns nothing. On the lexical path an empty text is simply a document with no terms.
The lexical path carries a postings index: a selective query scores only the documents that contain
its terms, while a query whose terms sit in most of the corpus falls back to a scan of the sorted
per-document term frequencies. Both compute the same BM25 score, so the hybrid changes cost, not
ranking. The dense path is an exact O(N × dim) scan and the dense store holds N × dim floats.
Neither path uses an ANN, and this model has no persistence in v1; the centroid remains the
O(categories × dim) alternative. On the
subject-only 20 Newsgroups split, BM25 KNN selected `K = 3` on a 15 % validation slice and read
**0.5073 / 0.4890** on test, against Naive Bayes's **0.5624 / 0.5293**; McNemar says Naive Bayes
is better (**p = 1.29e-12**, 318 documents KNN fixes against 525 it breaks). The run is pinned in
`reference/classification-20news-subject-knn.json`, so the loss is reproducible rather than
hidden. The dense path needs an E5 export and is not measured here.

## Question trees: classification and corpus division

`NaiveBayesClassifier` is a linear model over terms. Two question-tree builders sit beside it, and
neither knows what a term is: both read an `IFeatureSource<TItem>`, so the same tree can classify on
text, on bucketed time, on transitions, on derived flags — or on all of them at once through
`CompositeFeatureSource<TItem>`. The core sees features and weights, never a domain.

### Supervised: the most discriminative question first

```csharp
var classifier = new DecisionTreeClassifier();
classifier.Train(documents);                       // SearchDocument.Category

classifier.PredictBest("i cannot connect to the internet");
classifier.Explain("i cannot connect to the internet");
// contains "delivery" no -> contains "internet" yes -> Support
```

At each node the tree scores every candidate feature by information gain (or gain ratio, or
Gini), keeps the best positive one, and recurses. The first question is therefore the one that
separates the labels most. Ties are broken by ordinal feature order, so a corpus always produces
the same tree.

A question can carry a cost. `QuestionCost` maps a feature name to a positive number, and the
split score is divided by it, so a cheap question that is almost as discriminative wins over an
expensive one; the chosen split's `DecisionSplitMetrics.Cost` reports what it cost. The option is
passed through to every tree in the forest.

The tree can also decline. `MinLeafProbability` makes `Predict` and `PredictBest` return nothing
when the winning probability is below it, and `MinLeafSupport` does the same when the reached
leaf's summed weight is too small. `Explain` still returns the path and the leaf, so a caller can
see why the answer was withheld. The forest has `MinProbability` on its averaged distribution.

`ClassWeights` multiplies each document's sample weight by its category, so a majority class no
longer dominates the impurity or the leaves; a weight of zero removes the category. The same
option is passed to every tree in the forest.

A feature can be present, absent, or unknown. `FeatureSet.State` reports which; the binary
questions answer no for both absent and unknown, so the tree never silently reads a missing value
as a known false. `UnknownIndicatorSource` turns each unknown feature into an explicit
`unknown:<name>` feature, so a model that only asks binary questions can learn the difference
between "absent" and "could not tell"; the models stay binary. When an item's other dimensions
must not be trusted — the « generic » or « excluded » case — `MaskedFeatureSource` suppresses the
inner source and reports only the mask's features, so the tree learns a separate branch instead of
mixing the flag with the text.

The tree keeps a correction ledger. `Reinforce(text, category, weight)` adds signed mass to the
leaf the text reaches, `Unreinforce` removes it exactly, and `ForgetReinforcement` clears the
ledger; no retrain, and the tree structure is untouched. `Explain` shows the adjusted leaf, so the
path and the corrected distribution agree. `Train` clears the ledger, because the leaves it
replaced are gone.

`Explain` returns the questions a text answered and the leaf it reached; `Describe` renders the
whole tree. On the nine-document Support/Billing/Shipping corpus used by the tests:

```
contains "delivery"
  yes: -> Shipping (2)
  no:  contains "internet"
    yes: -> Support (2)
    no:  contains "invoice"
      yes: -> Billing (2)
      no:  -> Billing (3)
```

A leaf's `Probabilities` is the smoothed label mix of the training documents that reached it. It
is not a calibrated posterior: text whose features are all unknown still follows the false
branches to a leaf and gets an answer, and no abstention option is offered here. `Predict` and
`PredictBest` take the same `excludedCategories` set as Naive Bayes and renormalize over the rest.

### When the label matters more than the path: the forest

A single tree is the explainable model. On sparse text it is not the accurate one: measured on
20 Newsgroups, one tree reaches **0.446** accuracy against Naive Bayes's **0.757**. The forest is
the accuracy answer — bootstrap samples and random feature subsets, averaged — and it deliberately
has no `Explain`, because an ensemble's answer is not one path. It is a **bagged ensemble with a
global feature subset per tree**, not Breiman's per-split `sqrt(p)` sampling; it has no
out-of-bag estimate and no feature importance.

```csharp
var forest = new RandomForestClassifier(new RandomForestOptions
{
    Trees = 25,
    MaxDepth = 30,
    FeatureSubsetFraction = 0.5,
    Criterion = SplitCriterion.Gini,
});
forest.Train(documents);
forest.PredictBest("i cannot connect to the internet");
```

The same measurement puts the forest at **0.662** accuracy / **0.658** macro-F1, so it recovers
most of the single tree's gap but does not close it. The command and the pinned figures are in
[Evaluation](evaluation.md#classification-on-20-newsgroups).

### When the label is the point: the linear model

`LinearClassifier` is multinomial logistic regression over the same `IFeatureSource`, trained with
AdaGrad and a deterministic shuffle. It has no path, but it sees every feature at once and its
coefficients are readable through `TopFeatures`. On 20 Newsgroups it reads **0.8085** accuracy /
**0.8027** macro-F1 against Naive Bayes's **0.7573 / 0.7289**, the forest's **0.6621 / 0.6584**
and the tree's **0.4457 / 0.4945**. The three are not competitors: the tree explains, the forest
ensembles, the linear model decides. That ordering is a long-document result: on the same split
with only the `Subject` line (`--text subject`), the linear model reads **0.5533 / 0.5295** and
Naive Bayes **0.5624 / 0.5293** — the lead is gone and McNemar cannot separate them (p = 0.16).
Measure the baseline on short text before assuming the linear model is the answer.

```csharp
var linear = new LinearClassifier(new LinearClassifierOptions
{
    Epochs = 20,
    LearningRate = 0.1,
    L2 = 1e-4,
    MinDocumentFrequency = 2,
});
linear.Train(documents);
linear.PredictBest("i cannot connect to the internet");
linear.TopFeatures("Support", count: 5);
```

`Learn(text, category)` folds one labelled document into a correction ledger and returns a token;
`Unlearn(token)` removes exactly that contribution, without retraining the coefficients. It also
accepts a category the model never trained on: the category becomes **virtual**, scored with
`UnseenCategoryBias` (default `0`) plus the ledger mass on features already in the vocabulary. A
virtual category has no trained coefficient, so `TopFeatures` returns an empty list for it. The
limit is structural: a category whose terms are all outside the vocabulary cannot be learned
without a retrain, because there is nothing in the feature space to carry the mass.
`CorrectionWeight` sets how strongly one `Learn` moves the model; it is separate from
`LearningRate` (the AdaGrad step of `Train`) and from the per-item sample weight. `Train` clears
every virtual category and both ledgers. On a subject-only experiment that held five categories
out of training, virtual learning read **0.2918** on the held-out stream against **0.2884** for a
retrain every 50 documents, at 21 ms against 2 213 ms; the first occurrence of a new label is still
never predicted, because the model has not seen it yet. The command and the pin are in
[Evaluation](evaluation.md#a-category-that-was-never-in-the-training-set).

Two scaling knobs exist and both default off. `IdfWeighting` downweights binary features by
`log(1 + N/df)`; on 20NG it **loses** — 0.7753 accuracy / 0.7682 macro-F1 against 0.8085 / 0.8027
without — so it stays off. `TextFeatureSource(emitTermFrequency: true)` makes each term a
`FeatureKind.Count` feature, and `IdfWeighting` then applies `(1 + log(tf)) * idf`; that sublinear
TF-IDF also loses on 20NG — 0.7757 / 0.7676 — so binary presence remains the default.
`StandardizeNumericFeatures` z-scores numeric features so an hour and a frequency reach the model
on the same scale; it is unit-tested on a 1000–2100 corpus and not measured on 20NG, which has no
numeric features.

### Calibration

None of the distributions above is calibrated. `TemperatureCalibrator` fits one temperature on a
validation set by minimizing log-loss; it preserves the ranking — the argmax is the same at every
positive temperature — and softens or sharpens the confidence. `CalibratedClassifier.Fit(model,
validation)` wraps a trained classifier with the fitted temperature, so `PredictBest` is unchanged
and `Predict` reports the calibrated distribution. On 20 Newsgroups the linear model reads
**T = 1.04**, log-loss **0.7741 → 0.7650**, Brier **0.2855 → 0.2855** — its softmax was already
close. The forest's averaged probabilities are underconfident: **T = 0.35**, log-loss
**1.8156 → 1.2785**, Brier **0.6713 → 0.4701**. The tree's **T = 0.87** does not transfer — its
test loss is slightly worse — because a leaf's smoothed mix is structurally overconfident, not a
one-parameter error. One parameter cannot repair a model that is systematically wrong, and a
temperature fitted on one corpus need not transfer to another: measure log-loss on held-out data
after applying it.

The linear model has the same correction loop as the tree. `Reinforce(text, category, weight)`
adds signed mass to the item's features for the category, `Unreinforce` removes it exactly,
`ForgetReinforcement` clears the ledger, and `Train` clears it too. The correction is applied to
the logit with the same feature value the model uses (IDF or standardized), so it generalizes to
other items sharing the feature; an item with no feature in the vocabulary has no effect.
`TopFeatures` reports the training coefficients, not the ledger.

### Persisting a trained model

```csharp
using (var file = File.Create("classifier.lxcl"))
    ClassificationModelSerializer.Save(linear, file);

using (var file = File.OpenRead("classifier.lxcl"))
{
    var loaded = ClassificationModelSerializer.LoadLinear(file);
    loaded.PredictBest("i cannot connect to the internet");
}
```

The format is versioned binary (`LXCL`, version 2) and carries the trained state: the tree's
questions, the forest's trees, the linear model's coefficients and virtual categories, or the
centroid's sums, counts and global mean, plus the vocabulary, the prediction options and the
correction ledger. Version 1 is refused rather than guessed. `Load` dispatches on the stored kind and returns an
`ITextClassifier`; the typed loaders return the concrete type, so `Explain`, `TopFeatures` and
`Reinforce` survive the round trip. A version mismatch, the wrong kind, a bad magic or a truncated
file throws instead of guessing, and the stored feature-space hash is verified against the loaded
vocabulary.

The one thing the file cannot carry is the tokenizer, the embedding provider or the feature source
— a delegate is not data. Load with the same source and provider configuration you trained with,
or the same text maps to different features. The hash identifies the vocabulary for your own
bookkeeping; it cannot detect a source that changed while keeping the same names. A
nearest-neighbour model and a blend are not persisted: the first stores its corpus, and the second
is a graph of models and evidence sources. Save the lanes individually and rebuild the blend.

### Online updates

`Learn(document)` folds one labelled document into the model and returns a `long` token;
`Unlearn(token)` removes exactly that contribution and returns `false` for an unknown token. On the
tree the document changes the distribution of the leaf it reaches; on the linear model it adds
signed mass on its known features for its category. Neither rebuilds the structure or the
coefficients: this is an exact correction on top of a trained model, not a retrain, and `Train`
clears both ledgers.

```csharp
long token = linear.Learn("i cannot connect", "Support");
// ...
linear.Unlearn(token);   // the exact contribution is gone
```

Measured on the 20 Newsgroups train split (11 300 documents, tuned configuration), learning 2 260
documents one by one took **0.43 s** for the tree and **0.63 s** for the linear model; unlearning
them took **0.02 s** and **0.58 s**. A full retrain of the 11 300 in the same run took **14.1 s**
and **25.7 s**. At a few thousand documents a retrain is therefore affordable — seconds, not
minutes — but an online update is roughly one to two orders of magnitude cheaper. The two are not
interchangeable: `Learn` corrects the reached leaf or the feature logits and does not restructure
the tree or re-optimize the coefficients, so measure the accuracy you need rather than assuming
`Learn` equals `Train`.

### Unsupervised: dividing a corpus

```csharp
var division = new CorpusDivider().Divide(documents);
Console.WriteLine(division.Describe());
```

There are no labels. For each candidate feature, the items that carry it and the items that do not
become two weighted presence profiles, and the score is the balance-weighted mean squared
difference between them — the between-group variance of binary features, with the candidate's own
profile excluded so the score is about the rest. A rare feature that isolates one item scores low
because the balance factor is low; a feature every item carries scores zero.

`CorpusDivision.Clusters` lists the terminal regions in tree order, each with its document ids,
its top features by document frequency × idf, its depth and its summed weight.

### Several dimensions, and weights

A feature is an opaque name. Time is a bucket name or a number, a transition is a pair name, a
process is an identifier, and "generic" or "excluded" is another name; the caller's source decides.
A feature may also carry a number: the tree then scores thresholds over the values in the node, so
`hour >= 14` is a question it can learn — and it places 13 and 17 by the same inequality, which a
bucket named `hour:14` would not. A missing value answers no, so an item whose source could not
provide the value takes the false branch rather than being read as zero.

```csharp
IFeatureSource<Observation> features = new DelegateFeatureSource<Observation>(
    o => new FeatureSet(
        new[] { "term" },
        new Dictionary<string, double> { ["hour"] = o.Hour }));
```

Compose the dimensions with `CompositeFeatureSource<TItem>`:

```csharp
var features = new CompositeFeatureSource<Observation>(
    new DelegateFeatureSource<Observation>(o => new[] { $"bucket:{o.Bucket}" }),
    new DelegateFeatureSource<Observation>(o => o.IsGeneric ? new[] { "generic" } : Array.Empty<string>()));

var tree = new DecisionTreeClassifier<Observation>(
    features,
    o => o.Category,
    weightOf: o => Math.Pow(0.5, o.Age.TotalDays / halfLife.TotalDays));
```

The third argument is the whole half-life story. The core does not know what an age is: it knows
a non-negative sample weight, and every gain, impurity, leaf distribution and cluster profile is
weighted by it. A half-life is the caller's policy, computed once per item. The same argument
exists on `CorpusDivider<TItem>`.

### What it is not

- **Greedy, not optimal.** Finding the smallest tree that separates a labelled corpus is NP-hard.
  The builder takes the best split at each node; a corpus whose labels depend on an exclusive-or of
  two features has zero gain at the root and is not split at all.
- **Not calibrated.** A leaf distribution is a smoothed training mix, and the linear model's
  softmax is a distribution over labels, not a calibrated probability of correctness. Measure
  before thresholding either.
- **Measured on one public corpus, and the linear model wins.** On 20 Newsgroups the single tree
  reads 0.446 accuracy / 0.494 macro-F1, the forest 0.662 / 0.658, Naive Bayes 0.757 / 0.729, and
  `LinearClassifier` **0.8085 / 0.8027**. The tree and forest are explainability/ensemble options;
  the linear model is the accuracy one. The command and the pin are in
  [Evaluation](evaluation.md#classification-on-20-newsgroups), and the figures are one machine and
  one tokenizer, not a promise about your labels.
- **Bounded search.** The divider scores at most `MaxCandidateTerms` features per node over at most
  `MaxVocabularyTerms` profile features, both by decreasing document frequency. A discriminative
  feature outside the cap is not considered. The supervised tree has no such cap and scores every
  feature above `MinDocumentFrequency`.
