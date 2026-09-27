---
title: Text analysis
permalink: pretty
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
  `ITextClassifier` interface.

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
`NaiveBayesOptions` tunes the scoring: a softmax `Temperature` (sharpening/flattening), an
`IdfMode` (`None` / `DocumentCount` = `log(1 + N/df)` / `ClassCount` = `max(0, log(C/df))`),
an `Alpha` smoothing coefficient (optionally applied to the priors through `SmoothPriors`) and
`SkipOutOfVocabularyTokens` (ignore unknown query terms instead of a Laplace penalty). Setting
`Complement` switches to **Complement Naive Bayes** (Rennie et al. 2003, matching scikit-learn's
`ComplementNB`): each class is learned from the complement of its documents and a query is
attributed to the class whose exclusion explains it least — a cheap robustness win when the
training labels are heavily imbalanced. `Train` must not overlap any `Predict`; concurrent
`Predict` calls are safe.
