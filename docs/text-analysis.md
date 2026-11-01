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
