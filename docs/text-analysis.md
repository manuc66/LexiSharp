---
title: Text analysis
nav_order: 8
---

# Text analysis

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
  arrive over time and signed-weight reinforcement for per-search feedback.

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

### Reinforcement

`NaiveBayesClassifier` is also an `IReinforceableTextClassifier` (`classifier is
IReinforceableTextClassifier`), for feedback that arrives one search at a time and must not
cost a retrain:

```csharp
// A user says this search result is a Support request, and the model agrees on the next call.
classifier.Reinforce("i cannot connect to the internet", "Support", weight: 1.0);

// A user says it is *not* Billing. A negative weight pushes the category away, which is what
// "this is not that" means — and it needs no idea of what the category is instead.
classifier.Reinforce("monthly invoice", "Billing", weight: -1.0);

// Changed their mind, or the feedback was wrong: this subtracts exactly what Reinforce added.
classifier.Unreinforce("i cannot connect to the internet", "Support", weight: 1.0);

// Or drop every piece of feedback at once, back to the state Train left the model in.
classifier.ForgetReinforcement();
```

The weight is signed, so one method covers reinforcement, penalization and cancellation. Three
properties are contractual, and each has a test: the effect is proportional to the weight, it
accumulates over repeated calls, and each distinct term counts once however often the text
repeats it. `Train` never writes the ledger, so feedback survives a rebuild of the corpus model —
reinforcing a category the current corpus does not contain does nothing for as long as that is
true. `ForgetReinforcement` is how you say the evidence should not come back.

Two boundaries worth knowing, because both follow from the label set being the corpus's:
reinforcing a category that was never trained is a no-op (use `Train` to introduce a category),
and `Unreinforce` is not a check that the text was ever reinforced — the ledger is additive, so
it is the same operation as reinforcing by `-weight`, and unreinforcing something that was never
reinforced simply pushes the category further away.

The two capabilities are independent: learning a document leaves reinforcement alone, and
forgetting reinforcement leaves the corpus counts alone. `Learn` is corpus state, `Reinforce` is a
position laid over it.

`NaiveBayesOptions` tunes the scoring: a softmax `Temperature` (sharpening/flattening), an
`IdfMode` (`None` / `DocumentCount` = `log(1 + N/df)` / `ClassCount` = `max(0, log(C/df))`),
an `Alpha` smoothing coefficient (optionally applied to the priors through `SmoothPriors`) and
`SkipOutOfVocabularyTokens` (ignore unknown query terms instead of a Laplace penalty). Setting
`Complement` switches to **Complement Naive Bayes** (Rennie et al. 2003, matching scikit-learn's
`ComplementNB`): each class is learned from the complement of its documents and a query is
attributed to the class whose exclusion explains it least — a cheap robustness win when the
training labels are heavily imbalanced. `Train` must not overlap any `Predict`; concurrent
`Predict` calls are safe. None of `Learn`, `Unlearn`, `Reinforce`, `Unreinforce` or
`ForgetReinforcement` is safe alongside a `Predict`.
