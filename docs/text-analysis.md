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
  (E5, MiniLM, an HTTP endpoint — LexiSharp never runs the model), and
  `FusedTextClassifier` blends the lexical and semantic posteriors.

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
read lock is not measurably expensive here, though it is not free either.

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

What this does not do: there are no class priors (a frequent category is not favoured), no
incremental `Learn`/`Unlearn` (retrain instead), no persistence for the centroid model, and no
calibration between the two models. The temperature changes confidence, never the cosine ranking.
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
as a known false. When an item's other dimensions must not be trusted — the « generic » or
« excluded » case — `MaskedFeatureSource` suppresses the inner source and reports only the mask's
features, so the tree learns a separate branch instead of mixing the flag with the text.

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
has no `Explain`, because an ensemble's answer is not one path.

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
ensembles, the linear model decides.

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

### Calibration

None of the distributions above is calibrated. `TemperatureCalibrator` fits one temperature on a
validation set by minimizing log-loss; it preserves the ranking — the argmax is the same at every
positive temperature — and softens or sharpens the confidence. `CalibratedClassifier.Fit(model,
validation)` wraps a trained classifier with the fitted temperature, so `PredictBest` is unchanged
and `Predict` reports the calibrated distribution. On 20 Newsgroups the linear model reads
**T = 1.04**, log-loss **0.7741 → 0.7650**, Brier **0.2855 → 0.2855** — its softmax was already
close. One parameter cannot repair a model that is systematically wrong, and a temperature fitted
on one corpus need not transfer to another: measure log-loss on held-out data after applying it.

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
