# LexiSharp

[![CI](https://github.com/manuc66/lexisharp/actions/workflows/ci.yml/badge.svg)](https://github.com/manuc66/lexisharp/actions/workflows/ci.yml)
[![CodeQL](https://github.com/manuc66/lexisharp/actions/workflows/codeql.yml/badge.svg)](https://github.com/manuc66/lexisharp/actions/workflows/codeql.yml)
[![codecov](https://codecov.io/gh/manuc66/lexisharp/graph/badge.svg)](https://codecov.io/gh/manuc66/lexisharp)
[![SonarCloud](https://sonarcloud.io/api/project_badges/quality_gate?project=manuc66_lexisharp)](https://sonarcloud.io/summary/new_code?id=manuc66_lexisharp)
[![NuGet](https://img.shields.io/nuget/v/LexiSharp.svg)](https://www.nuget.org/packages/LexiSharp/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

> A lightweight lexical text search and classification library for .NET.

LexiSharp provides a small, dependency-free set of interfaces and implementations for
indexing plain text and retrieving/ranking/classifying documents **without any semantic
or ML model** — pure lexical statistics.

## Status & provenance

- **Young, single-maintainer project (v0.1.0).** No production track record and no external
  contributors yet; the public API may still change between minor versions. Evaluate it as
  such before adopting it.
- **Developed with AI assistance.** Most of the code and this README were written with LLM
  coding agents, then reviewed and tested by the maintainer. The techniques implemented
  (BM25, RRF, SPLADE-style sparse retrieval, MaxSim) follow established IR literature; this
  repository contributes no novel research.
- **Claims vs. evidence.** Behavior described in this README is covered by the xUnit suite
  (Postgres/ParadeDB integration tests self-skip without a live instance — see
  *Building & testing*). Comparative or performance statements are kept to a minimum; the
  few measured numbers live in [BENCHMARKS.md](BENCHMARKS.md) and are indicative only.
- **Not every combination is exercised.** Engines, scorers, rerankers and mergers are tested
  individually and in a few documented combinations, but the full cross-product is not:
  treat unusual pairings as *supported by construction, not yet stress-tested*.

## Features

- **Pluggable architecture**: an `ITextIndex`, `ITextScorer` and `ITokenizer` are
  independent contracts; algortihms can be swapped without touching the engine.
- **Four ranking strategies** behind the same `ITextSearchEngine`:
  - `Bm25Scorer` — Okapi BM25, with ready-made `Bm25Parameters` profiles (`Balanced`,
    `Aggressive`, `Conservative`),
  - `TfIdfScorer` — TF-IDF,
  - `QueryLikelihoodScorer` — probabilistic language model (Jelinek-Mercer smoothing),
  - `BooleanScorer` — exact AND/OR filter.
- **In-memory inverted index** (`InMemoryTextIndex`) with term positions, document
  frequencies, corpus statistics and incremental `Add`/`Remove`, plus an index statistics
  snapshot (`GetStatistics`: documents, vocabulary, tokens, average length, vocabulary richness).
- **Score boosting** (`BoostedTextSearchEngine`): a decorator that applies **signed** score
  adjustments (multiplicative factor and/or additive offset) per result — boost a category or a
  priority, damp or penalize stale matches — without touching the underlying engine.
- **Second-stage reranking**: an `IReranker` seam and the `RerankedTextSearchEngine`
  decorator (over-fetch, re-rank, guard rails) in the core; shipped rerankers include a
  diversity-preserving **MMR**, a **cascade** pipeline that chains any number of
  reranking stages with per-stage trimming, a **cross-encoder** reranker driven by a
  consumer-provided pairwise scoring model (`ICrossEncoderScorer`), and a **ColBERT MaxSim**
  reranker that re-scores a shortlist token-by-token with late interaction
  (`ITokenEmbeddingProvider`).
- **Sparse learned embeddings**: `SparseTextSearchEngine` and its `ISparseEmbeddingProvider`
  seam bring SPLADE/uniCOIL-style retrieval (.NET-core only, weights learned, inverted-index
  scoring kept) without pulling ONNX into the library — the model lives in the consumer.
- **Metadata filters**: declarative, AND-composed filters over document fields
  (`MetadataFilterOperator`: equal, not-equal, contains, numeric-or-ordinal greater/less than)
  in `SearchOptions` — honored by every backend (stock in-memory and SQL) before scoring.
- **Pagination**: `SearchOptions.Offset` cuts any window `[Offset, Offset + Limit)` of the
  ranking — honored by the stock engine, the boost/rerank decorators, the hybrid merger
  (the page comes from the merged ordering) and every SQL backend.
- **Phrase queries**: double-quoted segments (`"machine learning"`) must appear at
  consecutive document positions (several phrases are AND-ed), while the free terms around
  them keep scoring — free terms never hard-filter a mixed query. Natively honored by the
  stock engine (index positions), PostgreSQL (`websearch_to_tsquery`) and ParadeDB (`###`).
- **Highlighting** (`LexiSharp.Highlighting`): `TextHighlighter` wraps query matches in the
  original text (`HighlightFull`) or returns padded, word-snapped snippets (`Highlight`) —
  driven by the tokenizer's span mode (`TokenizeWithSpans`: term + `[Start, Length)` offsets
  into the source).
- **Prefix &amp; fuzzy queries**: `neural*` expands to every indexed term with that prefix,
  `catt~`/`catt~N` fuzzy-matches within N edits (default 1, clamped to 0–2) — search-time
  vocabulary expansion on the stock engine (`IVocabularyIndex`), 64 terms max per atom.
- **Synonyms** (`SynonymMap`): one-way rewrites and bidirectional equivalence groups,
  tokenized at engine construction and applied to free query terms — one level deep
  (non-transitive), never inside quoted phrases.
- **Facets** (`IFacetedSearchEngine`): `SearchWithFacets` returns the ranked page plus value
  counts per requested `Fields` entry over the whole match set — independent of
  `Offset`/`Limit`, ordered by count then value.
- **Span-first API**: the text entry points — `ITokenizer.Tokenize`/`TokenizeWithSpans`,
  `QueryParser.Parse`/`SplitRaw`, `ITextSearchEngine.Search`,
  `IFacetedSearchEngine.SearchWithFacets` and `RankedTextSearchEngine.Explain` — each have a
  `ReadOnlySpan<char>` overload that avoids materializing the query as a string; default
  interface implementations forward to the string path so existing implementers keep working.
- **Cost-based routing** (`RoutedSearchEngine`): opt-in decorator over several pre-filled
  engines that forwards each query to the cheapest one, chosen by an `IQueryCostEstimator` —
  the default `CheapestByCandidateCountEstimator` uses per-engine `IQueryCostProbe` estimates.
- **Lexical similarity** (`LexiSharp.Similarity`): pairwise token-set measures (Jaccard,
  Sørensen–Dice) over the library tokenizer, a `pg_trgm`-style character trigram similarity,
  and a rolling Levenshtein edit distance — near-duplicate detection and fuzzy matching with
  no index.
- **Keyword extraction** (`LexiSharp.Keywords`): a corpus-backed **TF-IDF** extractor
  (demotes corpus-frequent words) and a graph-based **TextRank** extractor (weighted
  co-occurrence graph + PageRank), both deterministic and tokenizer-configurable.
- **Explainable scoring**: `Bm25Scorer` implements `IScoreExplainer`, and
  `RankedTextSearchEngine.Explain` returns a per-term breakdown (TF, IDF, term score, length
  normalization, parameter values) of any ranking decision.
- **Evaluation metrics** (`RetrievalMetrics`): `Precision@k`, `Recall@k`, `F1@k`, binary and
  **graded** `nDCG@k` (exponential gains), plus `ReciprocalRank@k` (→ MRR) and
  `AveragePrecision@k` (→ MAP).
- **BM25 tuning**: `Bm25ParameterTuner` grid-searches `k1`/`b` against your own validation
  queries, judged by `Precision@k`, `Recall@k`, `F1@k` or `nDCG@k`.
- **Supervised classification** (`NaiveBayesClassifier`): multinomial Naive Bayes with
  Laplace smoothing, exposing a dedicated `ITextClassifier` interface.
- **Configurable tokenizer**: Unicode NFKD normalization and diacritics removal,
  lowercasing, optional stop-word removal, optional n-grams, and a pluggable
  `IStemmer` seam (no stemmer is bundled on purpose — bring your own, e.g. Snowball).
  Tokenization is SIMD-accelerated (`SearchValues` + `IndexOfAnyExcept`, with a
  `System.Text.Ascii` fast path in normalization).
- **Optional backends**, shipped as separate packages:
  - `LexiSharp.Postgres` — PostgreSQL backends implementing the same `ITextSearchEngine`:
    a lexical engine over `tsvector` + GIN + `unaccent`, an ANN engine over `pgvector`
    (HNSW/IVFFlat) driven by an external `IEmbeddingProvider`, a learned-**sparse** engine
    over `pgvector sparsevec` (HNSW) driven by an `ISparseEmbeddingProvider`, and an
    approximate **fuzzy** engine over the `pg_trgm` trigram extension (with optional
    `fuzzystrmatch` refinement);
  - `LexiSharp.ParadeDB` — **true Okapi BM25** on top of the `pg_search` Tantivy extension
    (AGPL-3, requires the ParadeDB Docker image or self-hosted extension).

## Quick start

```csharp
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;

ITextSearchEngine engine = new RankedTextSearchEngine(
    new InMemoryTextIndex(),
    new Bm25Scorer());

engine.Index(new[]
{
    new SearchDocument("1", "The search engine uses BM25 to rank the results"),
    new SearchDocument("2", "TF-IDF is a classic method of textual search"),
    new SearchDocument("3", "Italian cuisine is renowned in Rome"),
});

IReadOnlyList<SearchResult> results = engine.Search("textual search");

foreach (var result in results)
    Console.WriteLine($"{result.DocumentId} - {result.Score:0.###}: {result.Document.Text}");
```

Change the ranking algorithm without rebuilding the index:

```csharp
var bm25Engine   = new RankedTextSearchEngine(index, new Bm25Scorer());
var tfIdfEngine  = new RankedTextSearchEngine(index, new TfIdfScorer());
var booleanEngine = new RankedTextSearchEngine(index, new BooleanScorer(BooleanMatch.AllTerms));
```

### Classification

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

### Tokenizer customization

```csharp
using LexiSharp.Linguistics;

var tokenizer = new Tokenizer(new TokenizerOptions
{
    RemoveStopWords = true,       // English list, or provide StopWords.Create(...)
    NGramMax = 2,                 // produce unigrams + bigrams
    Stemmer = new MyStemmer(),    // implement IStemmer (French, Snowball, ...)
});
```

### Score boosting (`BoostedTextSearchEngine`)

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

### Reranking (`IReranker`, MMR, cascade)

Retrieve with recall, then re-rank a shortlist with precision. The core seam is `IReranker`;
`RerankedTextSearchEngine` decorates any engine (over-fetches, re-ranks, applies
`MinimumScore`/`Limit` on the final scores):

```csharp
using LexiSharp.Core;

IReranker reranker = ...;                                    // yours, or the MMR one below
ITextSearchEngine engine = new RerankedTextSearchEngine(baseEngine, reranker, maxCandidates: 100);
```

`LexiSharp` ships two built-in rerankers. **MMR** (Maximal Marginal Relevance)
re-orders candidates so each next pick is relevant *and* different from the picks before it —
near-duplicate results are pushed back; candidates without a vector are never penalized:

```csharp
using LexiSharp.Hybrid;

var vectors = new Dictionary<string, ReadOnlyMemory<float>>
{
    ["doc-1"] = embedding1, // pre-computed with your IEmbeddingProvider
    ["doc-2"] = embedding2,
};

IReranker mmr = new MaximalMarginalRelevanceReranker(vectors, lambda: 0.7, limit: 5);
```

**Cascade** chains any number of stages, trimming between stages so only the strongest
candidates reach the expensive final ones; it is itself an `IReranker`, so cascades nest:

```csharp
var pipeline = new CascadeRerankPipeline(
    new IReranker[] { lexicalReranker, mmr },
    new CascadeRerankOptions(StageLimit: 20, FinalLimit: 5, MinimumScore: 0.01));
```

**Cross-encoder** re-scores the shortlist with a pairwise model — a precision stage for cases
where whole-corpus scoring would be too expensive (ColBERT-style late interaction, an LLM
judge, ...). The model itself is a consumer-provided seam (`ICrossEncoderScorer`, same
contract as `IEmbeddingProvider`: LexiSharp never runs the model):

```csharp
IReranker cross = new CrossEncoderReranker(myOnnxCrossEncoder, limit: 5);
```

`CrossEncoderReranker` replaces each candidate's score with the model's, drops `0`/NaN/infinity
scores, and applies an optional `MinimumScore` and `Limit`.

**MaxSim** (ColBERT-style late interaction) re-scores the shortlist token-by-token instead of
as a single embedding: every token of the query is embedded, each scores against the whole
candidate's token embeddings (`max similarity per query token`, summed), so a query token never
has to "average itself away" across the document:

```csharp
using LexiSharp.Core;
using LexiSharp.Hybrid;

var tokenVectors = new Dictionary<string, IReadOnlyList<ReadOnlyMemory<float>>>
{
    ["doc-1"] = doc1TokenEmbeddings, // token embeddings pre-computed at index time
    ["doc-2"] = doc2TokenEmbeddings,
};

IReranker maxsim = new MaxSimReranker(
    myTokenEmbedder,                 // ITokenEmbeddingProvider (core seam, consumer-provided)
    tokenVectors,                    // doc-side token embeddings, pre-computed with the Passage role
    limit: 5,
    minimumScore: 0.0);
```

`MaxSimReranker` scores each candidate as `Σₜ max_tok cosine(q_t, d_tok)` — for each query token,
the best cosine against any of the candidate's token embeddings (the query side is re-embedded
with the `Query` role per search) — drops `0`/NaN/infinity scores
and ranks by total. It is the middle ground between whole-document cosine and the full
pairwise pass of a cross-encoder.

### Metadata filters

Gate the corpus with structured predicates over `SearchDocument.Fields` — every filter must
hold (AND), and filtering happens before scoring:

```csharp
var options = new SearchOptions(
    Limit: 10,
    Filters:
    [
        new MetadataFilter("kind", MetadataFilterOperator.Equal, "article"),
        new MetadataFilter("year", MetadataFilterOperator.GreaterThan, "2023"),
        new MetadataFilter("tags", MetadataFilterOperator.Contains, "nlp"),
    ]);

var results = engine.Search("vector search", options);
```

Comparisons are culture-invariant; greater/less-than go numeric when both sides parse as
numbers, otherwise ordinal. Documents missing a field fail everything except `NotEqual`.
Every backend honors the same contract: the in-memory engines evaluate the predicate before
scoring, and the SQL backends (PostgreSQL, ParadeDB) push it down as a parameterized
predicate over the `fields jsonb` column.

### Phrase queries

Quote a segment of the query to require its terms at consecutive document positions:

```csharp
var results = engine.Search("neural \"machine learning\"", new SearchOptions(Limit: 10));
```

Parsing happens before tokenization (`QueryParser`): double quotes are otherwise an ordinary
separator for the tokenizer. In a mixed query the phrase is a hard corpus gate while the free
terms around it only contribute to scoring — a document that matches the phrase comes back
even if it lacks every free term, and a document that only has the free terms never does.
Several quoted segments are AND-ed. A query made solely of empty quotes matches nothing.
Phrase checks assume a plain token stream: n-gram tokenizers emit overlapping tokens and
break the consecutive-position guarantee.

The SQL backends honor the same syntax natively: PostgreSQL through
`websearch_to_tsquery`, ParadeDB through the `###` phrase operator (on that backend only
phrases shape the match set when quotes are present — free terms stay out of `WHERE`, exactly
mirroring the stock engine's scoring-only role for them).

### Highlighting

Mark where a query matched inside a document — same normalization as the index, so hits land
on token boundaries even when the source text differs in case or accents:

```csharp
using LexiSharp.Highlighting;

var terms = Tokenizer.Default.Tokenize(query);   // or QueryParser.Parse(query, tokenizer).AllTerms

string marked = TextHighlighter.HighlightFull(document.Text, terms, Tokenizer.Default);
// "The <em>quick</em> brown fox jumps over the lazy dog"

IReadOnlyList<HighlightSnippet> snippets = TextHighlighter.Highlight(
    document.Text, terms, Tokenizer.Default,
    new HighlightOptions { MaxSnippets = 2, Padding = 30 });
```

Matching goes through the tokenizer's span mode (`ISpanTokenizer.TokenizeWithSpans`, built
into `Tokenizer`): each normalized term carries its `[Start, Length)` offsets into the source,
so the query must be tokenized with the same tokenizer. Nearby matches cluster into one
snippet, windows snap outward to word boundaries, and overlapping ranges (n-gram tokenizers)
merge before tagging.

### Prefix &amp; fuzzy queries

Suffix a free-text atom to expand it against the index vocabulary at search time:

```csharp
engine.Search("neural*");    // every indexed term starting with "neural"
engine.Search("catt~");      // within 1 edit: "cat", "cats", "catt", ...
engine.Search("catt~2");     // within 2 edits (count clamped to 0–2)
```

Expansion is a stock-engine feature: the atom's base is tokenized first, then matched against
an `IVocabularyIndex` (`InMemoryTextIndex` implements it), keeping at most 64 terms per atom —
highest document frequency first, then ordinal order. An index without vocabulary support
falls back to the atom's literal base term, i.e. the behavior of a query without operators.
Operators are only recognized when suffixed to word characters, never inside quoted phrases
(a `"machine*"` phrase stays literal), and they are a LexiSharp query syntax — the SQL
backends do not interpret them.

### Synonyms

Register synonym edges once, then every free query term pulls in its direct synonyms:

```csharp
using LexiSharp.Linguistics;

var synonyms = new SynonymMap()
    .Add("car", "auto")                       // one-way: "car" also searches "auto"
    .AddEquivalent("auto", "automobile");     // bidirectional group

ITextSearchEngine engine = new RankedTextSearchEngine(
    new InMemoryTextIndex(),
    new Bm25Scorer(),
    synonyms: synonyms);
```

Entries are tokenized with the engine's tokenizer at construction and each must reduce to
exactly one term (otherwise the constructor throws). Expansion is one level deep and
non-transitive — a synonym's own synonyms are never pulled in — and applies to free terms
only: quoted phrases stay literal. Like the prefix/fuzzy operators, this is a stock-engine
feature; the SQL backends do not rewrite queries.

### Facets

Get value counts for UI refinements alongside the ranked page:

```csharp
using LexiSharp.Core;

IFacetedSearchEngine engine = new RankedTextSearchEngine(index, new Bm25Scorer());

FacetedSearchResult page = engine.SearchWithFacets(
    "fast car",
    new SearchOptions(Limit: 10),
    facetFields: ["kind", "lang"]);

foreach (var bucket in page.Buckets)
    foreach (var value in bucket.Values)          // count desc, then value ordinal
        Console.WriteLine($"{bucket.Field}={value.Value}: {value.Count}");
```

`Results` is identical to `Search` for the same arguments. Counts cover every document that
passes the metadata filters, the phrase gates and the score thresholds — the whole match set —
independently of `Offset`/`Limit`, which only cut `Results`. A document missing a field does
not count for it (`Category` is never faceted), and fields no matching document carries are
omitted from `Buckets`. Stock engine only.

### Span-first API

The text entry points accept `ReadOnlySpan<char>`, so a query already living in a buffer need
not be copied into a `string` first:

```csharp
ReadOnlySpan<char> query = buffer.AsSpan(offset, length);

var parsed = QueryParser.Parse(query, Tokenizer.Default);
IReadOnlyList<SearchResult> hits = engine.Search(query, new SearchOptions(Limit: 10));
FacetedSearchResult page = engine.SearchWithFacets(query, facetFields: ["kind"]);
ScoreExplanation? why = engine.Explain("doc-1", query);
```

Every overload is equivalent to its `string` counterpart. `Tokenizer` runs the same pipeline
directly over the span (SIMD ASCII runs, rune decoding only on the non-ASCII path); other
implementers fall back to the default interface method, which copies the span and forwards. A
`null` literal still binds to the `string` overload, so the span path is null-free.

### Cost-based routing (`RoutedSearchEngine`)

When the same corpus is reachable through several engines — say a stock in-memory engine and a
SQL backend — route each query to the one that will do the least work:

```csharp
using LexiSharp.Core;

var router = new RoutedSearchEngine(new[]
{
    new RoutedEngine("memory", memoryEngine),   // implements IQueryCostProbe
    new RoutedEngine("postgres", pgEngine),     // no probe: last resort
});

IReadOnlyList<SearchResult> hits = router.Search("machine learning");
```

The router owns no index — it never writes, and its `Index`/`Add`/`Remove`/`Clear` throw
`NotSupportedException`; populate the engines yourself. Each query runs on exactly one engine,
so the scores are that engine's own (the router never mixes or renormalizes them across
engines). The default `CheapestByCandidateCountEstimator` asks every engine implementing
`IQueryCostProbe` for `EstimateCandidateCount` and picks the smallest — ties keep the earliest
engine — while engines without the probe are only used when no costed engine exists. The stock
engine's estimate is the sum of the literal query terms' document frequencies; pass a custom
`IQueryCostEstimator` to route on engine priority, latency history or query shape instead.

The router is capability-preserving: `SearchWithFacets`, `SearchWithDetails` and `Explain` run
on the engine the estimator selects for that query, and the router's own
`EstimateCandidateCount` reports the smallest estimate across its engines (so a routed engine can
itself be a candidate inside another router). Because the selection is per query, a capability
the selected engine lacks throws `NotSupportedException` rather than silently re-routing.
`BoostedTextSearchEngine`/`RerankedTextSearchEngine` also forward `IQueryCostProbe` to their
inner engine (they do not change how many candidates a query touches); their score-mutating
wrapper deliberately does not surface facets/detailed/explain, whose contracts it cannot
preserve.

### Lexical similarity and keyword extraction

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

### Index persistence (`LexiSharp.MessagePack`)

Save and reload an `InMemoryTextIndex` as compact, LZ4-compressed MessagePack binary —
documents (id, text, fields, category) **and** tokenizer configuration:

```csharp
// install once:  dotnet add package LexiSharp.MessagePack
using LexiSharp.MessagePack;

MessagePackTextIndexPersistence.Save(index, "corpus.bin");
var reloaded = MessagePackTextIndexPersistence.Load("corpus.bin"); // identical statistics, no re-indexing
```

A `Tokenizer` (stop words, n-grams, single-char terms) is reconstructed automatically. A custom
`ITokenizer` cannot be serialized: hand the same implementation to `Load` — a type-name check
protects against rebuilding with the wrong pipeline. Stemmed tokenizers likewise require the
original tokenizer at load time (stemmers are not serializable).

The same package persists a sparse engine through `MessagePackSparseIndexPersistence`: the
stored corpus is the documents **plus their learned weights**, so reloading bypasses the model —
only queries need the `ISparseEmbeddingProvider` again:

```csharp
MessagePackSparseIndexPersistence.Save(sparseEngine, "splade.bin");
var reloaded = MessagePackSparseIndexPersistence.Load("splade.bin", mySplade); // exact same search scores
```

### Explainable scoring and BM25 tuning

Audit any ranking decision term by term, then let the corpus pick its own parameters:

```csharp
var engine = new RankedTextSearchEngine(index, new Bm25Scorer());
ScoreExplanation? why = engine.Explain("doc-1", "search engine");
// why.Terms -> per-term TF, IDF and score contribution; why.LengthRatio, why.Parameters...

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

### PostgreSQL backend (`LexiSharp.Postgres`)

Persistent, shared, concurrent search on top of a classic PostgreSQL setup. Several engines,
all implementing `ITextSearchEngine` and sharing the same documents table (so the hybrid
engine can fan out and merge lexical + vector + fuzzy results with
`ReciprocalRankFusionMerger`):

**Lexical (`PostgresTextSearchEngine`)** — full-text over `tsvector`:

```csharp
// install once:  dotnet add package LexiSharp.Postgres
using LexiSharp.Postgres;

ITextSearchEngine engine = new PostgresTextSearchEngine(
    "Host=db;Port=5432;Username=app;Password=secret;Database=search");

engine.Add(new SearchDocument("1", "the quick brown fox jumps over the lazy dog",
    new Dictionary<string, string> { ["kind"] = "fable" }, "fable"));
```

The provider installs (idempotently) the `unaccent` extension, a documents table with a
`tsv tsvector` column and a GIN index, then queries it with `websearch_to_tsquery` and ranks
with `ts_rank_cd`. Combined with the `simple` config, `unaccent` mirrors LexiSharp's
accent-insensitive normalization. Scores are PostgreSQL-native, so they are **not** numerically
comparable to `Bm25Scorer`/`TfIdfScorer` — feed both backends into the hybrid engine below
when you need one consistent ordering.

**Vector (`PostgresVectorSearchEngine`)** — ANN over `pgvector` (HNSW or IVFFlat), fed by
your own embeddings:

```csharp
ITextSearchEngine vector = new PostgresVectorSearchEngine(
    "Host=db;Port=5432;Username=app;Password=secret;Database=search",
    new MyEmbeddingProvider(),                                // your ONNX/model-server deps, never LexiSharp
    new PostgresVectorOptions { Dimension = 384, Distance = VectorDistance.Cosine });

vector.Add(new SearchDocument("1", "the quick brown fox ..."));
vector.Search("a fast fox");   // scores: cosine→1-dist, L2→1/(1+dist), inner product→-dist
```

The engine installs (idempotently) the `vector` extension, adds an `embedding vector(D)`
column and an HNSW (or IVFFlat) index on the same documents table. IVFFlat needs rows to
cluster lists, so the index is created on the first `EnsureSchema()` call **after** your
first inserts. ANN results are approximate: combine with `HybridTextSearchEngine` + RRF to
trade recall for speed — exact cosine behavior is verified in the integration suite.

The embedding seam is role-aware: `PostgresVectorSearchEngine` embeds indexed documents with
`EmbeddingUse.Passage` and queries with `EmbeddingUse.Query`, so asymmetric models (E5 prefixes
and friends) work through the same single provider method. Finer knobs live in the options
(see the class docs): `HnswEfSearch` (per-search candidate list; the engine wraps the query in
a `SET LOCAL hnsw.ef_search` transaction), `EmbeddingTextField` (embed a named
`SearchDocument.TextFields` entry instead of `Text`, leaving `content`/`tsv` untouched for the
lexical engine) and, for a title/description (or any multi-section) split,
`EmbeddingColumns` — one `{suffix}_embedding vector(D)` column and ANN index per configured
`TextFields` entry. A search then targets a subset of columns
(`SearchWithColumns`) or all of them, OR-fusing candidates by best per-column similarity, and
`SearchWithDetails` (`IDetailedSearchEngine`) exposes each column's contribution for UI badges
or telemetry. The engine also implements `IListableSearchEngine`, so the full set of stored
ids can be streamed (`ListDocumentIds` / `ListDocumentIdsAsync`, keyset pagination) to diff
against an external ledger.

By default the integration tests are skipped unless `POSTGRES_TEST_CONNECTION` points at a
live instance (e.g. `Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=lexisharp`).
The vector and sparse tests additionally require the `vector` extension: use the
`pgvector/pgvector:pg16` image (lexical tests only need stock PostgreSQL).

**Sparse (`PostgresSparseSearchEngine`)** — learned-sparse ANN over `pgvector sparsevec`
(HNSW only — IVFFlat is unavailable for `sparsevec`), fed by your own sparse model:

```csharp
ITextSearchEngine sparse = new PostgresSparseSearchEngine(
    connectionString,
    mySpladeProvider,                                       // ISparseEmbeddingProvider, never LexiSharp
    new PostgresSparseOptions
    {
        Vocabulary = vocabulary,                            // term → coordinate, fixed up front
        Distance = SparseDistance.InnerProduct,             // default; dot product suits SPLADE
    });

sparse.Add(new SearchDocument("1", "the quick brown fox ..."));
sparse.Search("a fast fox");   // scores: inner product→-dist, cosine→1-dist, L2/L1→1/(1+dist)
```

The engine installs (idempotently) the `vector` extension, adds a `sparse sparsevec(D)` column
and an HNSW index on the same shared documents table. Because `sparsevec` is a positional
format, the **vocabulary (term → coordinate) is an index-layout decision**: it must be fixed
once and shared between the provider at index time and the one at query time. Terms outside the
vocabulary are ignored; a query with no known term returns nothing. HNSW — unlike IVFFlat —
works on empty tables and supports inserts, so there is no "index after first batch" step.
`sparsevec` caps a vector at 1000 non-zero elements. Scores are PostgreSQL-native and again
depend on the chosen distance; route through `ReciprocalRankFusionMerger` when mixing with the
lexical engine.

**Fuzzy (`PostgresFuzzySearchEngine`)** — approximate, typo-tolerant matching over `pg_trgm`
trigrams, with optional `fuzzystrmatch` (edit distance + phonetics):

```csharp
ITextSearchEngine fuzzy = new PostgresFuzzySearchEngine(connectionString, new PostgresFuzzyOptions
{
    SearchMode = TrgmSearchMode.Nearest,        // kNN: closest labels first (autocomplete)
    // SearchMode = TrgmSearchMode.Similarity,  // threshold: content % query (de-dup, did-you-mean)
    SimilarityThreshold = 0.3,                  // honored via set_limit() in Similarity mode
    UseLevenshteinRefinement = true,            // exact edit-distance post-filter
    IncludePhonetic = true,                     // metaphone column; phonetic matches (Similarity mode)
});
fuzzy.Index(new[]
{
    new SearchDocument("1", "katherine"),
    new SearchDocument("2", "catherine"),
});
fuzzy.Search("caterin");   // typo-tolerant: both labels come back
```

The engine installs (idempotently) `pg_trgm` (+ `fuzzystrmatch` when enabled) and **GiST and
GIN trigram indexes** on the same shared documents table. Scores are trigram similarities in
`[0, 1]` (1 identical, 0 no shared trigram → excluded, honoring the library's score-0
convention). `Nearest` mode orders with the GiST kNN operator (`content <-> query`); `Similarity`
mode ranks by `similarity()` above the configured threshold. These are the classic building
blocks for autocomplete, de-duplication of names/addresses and "did you mean". PostgreSQL-native
scores again call for `ReciprocalRankFusionMerger` when mixing with other engines.

### ParadeDB backend (`LexiSharp.ParadeDB`)

Okapi BM25 ranking computed by Tantivy inside PostgreSQL through the `pg_search`
extension — an option to consider when `ts_rank_cd` ranking is not good enough and true
BM25 is wanted.

```csharp
// install once:  dotnet add package LexiSharp.ParadeDB
using LexiSharp.ParadeDB;

ITextSearchEngine engine = new ParadeDBTextSearchEngine(connectionString);
engine.Add(new SearchDocument("1", "the quick brown fox jumps over the lazy dog"));
```

The engine installs (idempotently) the `pg_search` extension and a ParadeDB index
(`USING paradedb`, the renamed `USING bm25`) **on the same shared documents table** as the
Postgres engines, then matches with the `|||` disjunction operator and ranks with
`pdb.score(id)` — real BM25 (Tantivy variant), unlike `ts_rank_cd`. The default content
tokenizer (`pdb.simple` with ASCII folding) mirrors LexiSharp's diacritic-insensitive,
lowercase normalization:

```csharp
new ParadeDBTextSearchEngine(
    connectionString,
    new ParadeDBOptions { ContentTokenizer = "pdb.simple('ascii_folding=true')" });
```

BM25 scores are PostgreSQL-native, so they are **not** numerically comparable to
`Bm25Scorer`/`TfIdfScorer` — use the hybrid engine's `ReciprocalRankFusionMerger` (or
re-rank) for a single cross-engine ordering. Note the extension is **AGPL-3 licensed**: fine
for SaaS/internal use, but review it if you distribute the stack.

By default the tests are skipped unless `POSTGRES_TEST_CONNECTION` points at a live instance
**with `pg_search` available** (the `paradedb/paradedb:pg16` image ships it, preloaded) —
they self-skip when the extension is absent.

### Hybrid engine

Federate a **hot** in-memory index and a **cold** persistent backend, and produce one
consistent global ranking:

```csharp
using LexiSharp.Core;
using LexiSharp.Hybrid;
using LexiSharp.Indexing;
using LexiSharp.Ranking;

ITextSearchEngine hybrid = new HybridTextSearchEngine(new ITextSearchEngine[]
{
    new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer()), // hot subset
    postgresEngine,                                                       // cold backend
});

IReadOnlyList<SearchResult> results = hybrid.Search("textual search");
```

`HybridTextSearchEngine` queries every engine, de-duplicates the candidates by document id,
then re-ranks the whole union with a single scorer (`RerankingResultMerger`, default BM25).
Writes fan out to every engine. Three merge strategies are available:

| Merger | Behavior | Best for |
| --- | --- | --- |
| `RerankingResultMerger` (default) | re-scores the union with one `ITextScorer` | comparable stats, identical score scale wanted |
| `ReciprocalRankFusionMerger` | `Σ 1/(k + rank)` (k=60), rank-only | engines with **incomparable scales** — lexical + vector, ts_rank_cd vs BM25 (Postgres vs ParadeDB vs in-memory) |
| `WeightedScoreResultMerger` | normalized per-engine score blend | native scores trusted, per-engine weights wanted |

Reciprocal Rank Fusion never looks at scores, so it bridges engines whose scores are not
comparable — the sparse and dense embedding backends land in the same formula without
calibration.

For score breakdowns, `SearchWithDetails()` returns `DetailedSearchResult`s where each
document also carries its raw per-source score (`Contributions`), keyed by the labels passed
as `sourceNames` to the constructor (`"lexical"`, `"semantic"`, ... — default `"engine-N"`).
A source that did not return the document is simply absent from that dictionary; the merged
ordering from `Search()` is unchanged.

**Embeddings are an agreed seam, not a feature here**: `IEmbeddingProvider` (core) describes how a
consumer project (ONNX model, model server, ...) would produce vectors — LexiSharp never
computes embeddings — and `VectorSimilarity` provides pure cosine math. `PostgresVectorSearchEngine`
is the reference consumer: it turns any provider into an ANN backend that the same
`HybridTextSearchEngine` merges exactly like a lexical engine.

### Sparse learned embeddings (`ISparseEmbeddingProvider`, `SparseTextSearchEngine`)

SPLADE-style models (SPLADE, uniCOIL, ...) produce **sparse** learned vectors: a handful of
`term → weight` pairs where the weights are learned instead of tf-idf/BM25 frequencies.
The key insight of this family is that it does **not** replace the inverted-index infrastructure,
only the scoring function. `SparseTextSearchEngine` applies exactly that: it keeps a classic
`term → document → weight` inverted index, and a query scores each document by sparse
**dot product** `Σ_t w_q(t)·w_d(t,d)` — only the terms the learned model activated are visited:

```csharp
using LexiSharp.Core;
using LexiSharp.Indexing;

ISparseEmbeddingProvider splade = myOnnxSplade; // consumer-provided, incl. vocab mapping
ITextSearchEngine engine = new SparseTextSearchEngine(splade);

engine.Add(new SearchDocument("doc-1", "sparse retrievers beat dense on exact terms"));

var results = engine.Search("learned sparse retrieval");
```

Like `IEmbeddingProvider`, `ISparseEmbeddingProvider` is a pure seam in the core: the ONNX model,
tokenizer and vocabulary live in the consumer — and the `EmbeddingUse` role is threaded through
uniformly across the dense/sparse/token seams (`Passage` at index time, `Query` per search),
so asymmetric sparse variants keep a hook even though most SPLADE models are symmetric.
A walkthrough of writing a SPLADE provider
(ONNX + HuggingFace tokenizer + vocabulary mapping) is in
[docs/SPLADE.md](docs/SPLADE.md) — an outline, not a tested reference implementation. Weights
are expected non-negative (ReLU-like); non-positive values are treated as "term absent". The
engine implements `ITextSearchEngine`, so
it drops straight into `HybridTextSearchEngine` where it merges with BM25 and dense engines via
`ReciprocalRankFusionMerger` — RRF keeps sparse-only hits (matching terms the lexical scorer and
the dense cosine disagree on) that a BM25 re-scoring merge would drop.

The engine never re-embeds on reload: `Export()` / `Import()` decouple inference from
persistence, `MessagePackSparseIndexPersistence` serializes the stored weights directly, and
`PostgresSparseSearchEngine` is the same model over `pgvector sparsevec`. In every backend, only
**queries** keep needing the provider after the corpus is loaded.

### Reranking stage

The reranking stage composes with all of it: wrap the hybrid in a
`RerankedTextSearchEngine` (core decorator) and pass a `CascadeRerankPipeline`, a
`MaximalMarginalRelevanceReranker`, a `CrossEncoderReranker` or a `MaxSimReranker` to add a
precision or diversity pass on top of the fused ranking — the same two-stage
retrieve-then-rerank shape, one line of composition.

## Architecture

```
Package            Responsibilities
─────────────────────────────────────────────────────────────────────────────
LexiSharp         records + interfaces + in-memory index + scorers + tokenizer + IEmbeddingProvider + ISparseEmbeddingProvider + sparse engine (export/import) + boost/rerank decorators + filters + highlighting + facets + similarity + keywords + metrics + hybrid federation (RRF, weighted, cascade, cross-encoder, MMR, MaxSim)
LexiSharp.Postgres  PostgreSQL providers: tsvector+unaccent (lexical), pgvector ANN (vector), pgvector sparsevec (sparse), pg_trgm+fuzzystrmatch (fuzzy)
LexiSharp.ParadeDB   true BM25 provider on the pg_search (Tantivy) extension
LexiSharp.MessagePack   MessagePack (binary) persistence for the in-memory index and the sparse engine
```

Within the core package, separation of concerns mirrors the recommendations the library
was designed from:

- the **index** owns corpus statistics (tf, df, document length, positions, vocabulary);
- the **scorer** is a pure strategy reading from the index;
- the **engine** orchestrates query tokenization, scoring, filtering and ranking.

### Scoring conventions

- A score of exactly `0` means *not a match* and the document is excluded from results
  (all built-in scorers honor this).
- Every sub-system is culture-agnostic; text is normalized to lowercase without accents
  so that `"Résumé"` and `"resume"` match.

## Building & testing

```bash
dotnet build LexiSharp.slnx
dotnet test  tests/LexiSharp.Tests                # xUnit suite (Postgres tests need POSTGRES_TEST_CONNECTION)
dotnet run  --project bench/LexiSharp.Benchmarks  # BenchmarkDotNet suite (published numbers: BENCHMARKS.md)
```

Postgres/ParadeDB integration tests run against whatever `POSTGRES_TEST_CONNECTION` points to:
`pgvector/pgvector:pg16` covers the lexical + vector + sparse + fuzzy suites,
`paradedb/paradedb:pg16` covers the lexical + ParadeDB (BM25) + fuzzy suites. The sparse tests
self-skip when the `vector` extension is unavailable. The fuzzy tests
self-skip when `pg_trgm` (and `fuzzystrmatch`, when exercised) are unavailable.

## License

MIT