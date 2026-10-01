---
title: API reference
nav_order: 15
description: >-
  Every public type in the four packages, generated from the compiled assemblies and
  the XML documentation they ship: the contracts you implement, and the classes that
  implement them for you.
---

# API reference

The public surface, generated from the assemblies and the XML documentation they ship.
The **What it is** column is the type's own summary, not a sentence written for this
page: it is what the author wrote above the declaration, trimmed to its first
paragraph. Where it is not enough, the **Guide** column is the page that argues the
case.

This page indexes the surface; it does not teach it. The [guide](index.md) is where a
pipeline is built, and the API reference answers the narrower question of what exists
to build it with.

## The contracts

Every interface the library asks you to satisfy, and the ones it asks you to satisfy
*something* with. These are the seams: an engine, a scorer, a merger, a reranker, a
model provider. Implementing one is how the library is extended, which is why they
are listed first.

### Core package

| Contract | Extends | What it is | Guide |
| --- | --- | --- | --- |
| `ICandidateIndex` | `ITextIndex` | Capability for an index that can enumerate only the documents containing at least one of the given query terms, instead of scanning the whole corpus. | [guide](reference.md) |
| `IChunkContextEnricher` | — | Augments the searchable text of a SearchDocument before it is indexed — the « contextual retrieval » seam. A chunk that only says "the benefit rose 12%" is… | [guide](reference.md) |
| `ICrossEncoderScorer` | — | Scores a (query, document) pair as a single relevance value — the job of a cross-encoder (concatenate query and document into one input) or, more generally, of… | [guide](reference.md) |
| `IDetailedSearchEngine` | — | Optional capability of an ITextSearchEngine that can return, alongside the merged ranking, the per-source score breakdown that fed it (see Contributions). | [guide](reference.md) |
| `IEmbeddingProvider` | — | Contract for producing text embeddings. LexiSharp never computes embeddings itself: implementing this interface is up to the consumer — a local ONNX model, an… | [guide](reference.md) |
| `IExplainableSearchEngine` | — | Optional capability of an ITextSearchEngine that can explain why a document received the score it did for a query, when its scorer is an IScoreExplainer. | [guide](reference.md) |
| `IFacetedSearchEngine` | `ITextSearchEngine` | Optional capability of an ITextSearchEngine that can return, alongside the ranked results, facet counts over Fields. | [guide](reference.md) |
| `IIncrementalTextClassifier` | `ITextClassifier` | Optional capability of an ITextClassifier: add or retract single labelled documents without a retraining pass over the whole corpus. Detected with pattern… | [guide](reference.md) |
| `IListableSearchEngine` | — | Optional capability of a ITextSearchEngine that can enumerate the identifiers of the documents it currently holds, without materializing the full documents. | [guide](reference.md) |
| `IQueryCostEstimator` | — | Picks which of several engines a RoutedSearchEngine should run a query on. | [guide](reference.md) |
| `IQueryCostProbe` | — | Optional capability of an ITextSearchEngine that can estimate, cheaply and without running a search, how many documents a query is likely to touch — the signal… | [guide](reference.md) |
| `IQueryRouter` | — | Picks which route (engine + optional filters) a RoutingSearchEngine should run a query on — a semantic/intent decision, as opposed to IQueryCostEstimator which… | [guide](reference.md) |
| `IQuerySyntaxSupport` | — | Declares which QueryFeatures an ITextSearchEngine interprets, so callers can discover the supported query syntax instead of discovering it by getting wrong… | [guide](reference.md) |
| `IReinforceableTextClassifier` | `ITextClassifier` | Optional capability of an ITextClassifier: adjust a trained model from user feedback, one text-to-category association at a time, without a retraining pass over… | [guide](reference.md) |
| `IReranker` | — | Contract for re-ordering a list of candidate results after retrieval — the seam for second-stage ranking strategies (diversity preservation, cross-encoders,… | [guide](reference.md) |
| `IRetrievalMetrics` | — | Receives the measurements a RetrievalTelemetry takes. Implement it to ship numbers to a metrics backend (OpenTelemetry, Prometheus, StatsD, ...). | [guide](reference.md) |
| `IScoreExplainer` | `ITextScorer` | Optional capability of an ITextScorer that can justify its scores: it produces a ScoreExplanation decomposing the contribution of every matched query term. | [guide](reference.md) |
| `ISparseEmbeddingProvider` | — | Contract for producing sparse text embeddings — e.g. SPLADE/SPLADE-v2, uniCOIL, or any learned sparse model. Sparse embeddings map vocabulary terms to weights,… | [guide](reference.md) |
| `ITermOverlapScorer` | `ITextScorer` | Capability for a scorer whose score is exactly 0 for every document sharing no query term with the query, which lets the search engine skip such documents… | [guide](reference.md) |
| `ITextClassifier` | — | Supervised text classifier trained on labelled documents. | [guide](reference.md) |
| `ITextIndex` | — | An index of tokenized documents exposing the statistics needed by text scorers and classifiers (term frequency, document frequency, document length, ...). | [guide](reference.md) |
| `ITextScorer` | — | Computes how relevant a single document is with respect to a tokenized query, given a shared ITextIndex for corpus statistics. | [guide](reference.md) |
| `ITextSearchEngine` | — | The public search surface of the library: index documents and rank them against a query. | [guide](reference.md) |
| `ITokenEmbeddingProvider` | — | Produces a sequence of token-level embeddings for a text — the building block of late interaction (ColBERT-style) ranking, where a query is matched to a… | [guide](reference.md) |
| `IVocabularyIndex` | `ITextIndex` | Capability for an index that can enumerate its vocabulary — the seam prefix/fuzzy query expansion reads at search time. | [guide](reference.md) |
| `IWeightedPredictor` | — | Optional capability of a ITextClassifier that accepts WeightedTokens instead of raw text, bypassing internal tokenization. Detected with pattern matching… | [guide](reference.md) |
| `ITermExpander` | — | Derives the additional terms to index for a document from its own tokenization. | [guide](embeddings.md) |
| `IResultMerger` | — | Strategy that turns several ranked result lists — one per source engine — into a single coherent, de-duplicated, ordered list. | [guide](pipelines.md) |
| `IKeywordExtractor` | — | Strategy for pulling the most representative terms out of a single text — tag generation, query expansion seeds, summary highlights. | [guide](text-analysis.md) |
| `ISpanTokenizer` | `ITokenizer` | A tokenizer that can also report where each normalized term sits in the source text — the basis for search-result highlighting. | [guide](text-analysis.md) |
| `IStemmer` | — | Pluggable stemmer used by the Tokenizer to fold inflected forms onto a common stem. The library ships one implementation, PorterStemmer (English, no… | [guide](text-analysis.md) |
| `ITokenizer` | — | Converts raw text into a list of normalized terms. | [guide](text-analysis.md) |

### `LexiSharp.Postgres` package

| Contract | Extends | What it is | Guide |
| --- | --- | --- | --- |
| `IColumnAwareEmbeddingProvider` | `IEmbeddingProvider` | Optional capability of an IEmbeddingProvider used by a multi-column PostgresVectorSearchEngine: the provider is told which embedding column the text is being… | [guide](backends.md) |

## The types

Everything else that is public, by namespace. `record`, `record struct` and `class` are
told apart by the members the compiler adds to each — the difference is in the page
because `SearchDocument` is constructed and `InMemoryTextIndex` is not, and because a
`record struct` is neither: it is a value type with value equality and a `with`
expression, and calling it a `struct` would be as wrong as calling a `record` a class.

### Core package

#### `LexiSharp`

| Type | Kind | Contracts | What it is |
| --- | --- | --- | --- |
| `LexiSharpFacetedResult<TDocument>` | record | — | Ranked hits plus one facet bucket per requested field, as returned by LexiSharpIndex. |
| `LexiSharpHit<TDocument>` | record | — | One search hit over a user document, as returned by LexiSharpIndex. |
| `LexiSharpIndex<TDocument>` | class | — | The drop-in entry point of the library: an opinionated, typed search index that maps your own documents to the engine internals and back — from "a list of… |
| `LexiSharpIndexOptions<TDocument>` | class | — | Configuration of a LexiSharpIndex: how caller documents map to indexable records, and which engine pieces to use. |
| `LexiSharpQueryOptions` | record | — | Per-search options for LexiSharpIndex and friends. |

#### `LexiSharp.Benchmarking`

| Type | Kind | Contracts | What it is |
| --- | --- | --- | --- |
| `BenchmarkComparer` | class | — | Builds a BenchmarkComparison from two runs. |
| `BenchmarkComparison` | record | — | A baseline-versus-candidate comparison, per query and in aggregate. This is the artifact that answers "what did this change actually do", which a pair of means… |
| `BenchmarkConfig` | record | — | A named recipe for building the search engine a benchmark run evaluates. The build closure receives the shared index and the query set so a configuration can… |
| `BenchmarkConfigResult` | record | — | The outcome of benchmarking one BenchmarkConfig against a corpus and a query set: the retrieval metrics averaged over the judged queries, plus the latency… |
| `BenchmarkDeltaVerdict` | enum | — | How a query's nDCG moved between two configurations. |
| `BenchmarkMetrics` | record | — | Mean retrieval metrics of one benchmark configuration over the judged queries, at the configured topK. Every value lives in [0, 1]; all numerators and… |
| `BenchmarkOptions` | record | — | Tuning knobs of a Run. |
| `BenchmarkQuery` | record | — | One labeled query of a benchmark run: a raw query together with the relevance judgments a good ranking should reproduce. |
| `BenchmarkQueryDelta` | record | — | One query's movement between two benchmark runs, with the evidence for it. |
| `BenchmarkQueryResult` | record | — | The outcome of benchmarking one query: its own metrics, the ids the engine returned, and where the first judged document landed. |
| `CorpusBenchmark` | class | — | Runs a retrieval benchmark: builds one shared in-memory index over a corpus, evaluates every requested BenchmarkConfig against the same query set, and reports… |

#### `LexiSharp.Classification`

| Type | Kind | Contracts | What it is |
| --- | --- | --- | --- |
| `IdfMode` | enum | — | How term evidence is scaled by corpus rarity during classification. Term frequency is always counted from the training corpus; this knob decides whether (and… |
| `NaiveBayesClassifier` | class | `IIncrementalTextClassifier`, `IReinforceableTextClassifier`, `ITextClassifier`, `IWeightedPredictor` | Multinomial Naive Bayes trained on labelled documents. |
| `NaiveBayesOptions` | record | — | Tunable knobs for NaiveBayesClassifier; the defaults reproduce the classic Laplace-smoothed, unweighted multinomial Naive Bayes. |
| `SynchronizedTextClassifier` | class | `IDisposable`, `IIncrementalTextClassifier`, `IReinforceableTextClassifier`, `ITextClassifier`, `IWeightedPredictor` | Wraps an IReinforceableTextClassifier so that concurrent prediction and concurrent mutation stop being the caller's problem: any number of Predict and… |

#### `LexiSharp.Core`

| Type | Kind | Contracts | What it is |
| --- | --- | --- | --- |
| `BoostedTextSearchEngine` | class | `IQueryCostProbe`, `ITextSearchEngine` | A decorator engine that boosts or damps the matches of an inner engine after ranking. |
| `CheapestByCandidateCountEstimator` | class | `IQueryCostEstimator` | Picks the engine with the smallest EstimateCandidateCount. Engines that do not implement IQueryCostProbe cannot be costed and are only used when no probed… |
| `ClassificationResult` | record | — | A category predicted for a piece of text, with its estimated probability. |
| `DetailedSearchResult` | record | — | A ranked document whose final score is broken down by the source signal that produced it. |
| `EmbeddingProviderExtensions` | class | — | Backwards-compatible call shape: embeds text as a Passage. |
| `EmbeddingUse` | enum | — | The role a piece of text plays when it is embedded. Several models encode queries and documents differently by construction (e.g. E5 prefixes queries with… |
| `EngineRetrievalMetrics` | record | — | Aggregated measurements for one engine. |
| `FacetBucket` | record | — | Value counts for one facet field. |
| `FacetValue` | record | — | A distinct field value and how many counted documents carry it. |
| `FacetedSearchResult` | record | — | Ranked results plus one bucket per requested facet field. |
| `InMemoryRetrievalMetrics` | class | `IRetrievalMetrics` | The default IRetrievalMetrics: fixed-size counters kept in memory, with no external dependency. Enough to assert on in tests and to back a diagnostics endpoint… |
| `IndexRetrievalMetrics` | record | — | The latest observed size of one engine's index. |
| `MetadataFilter` | record | — | A declarative, single-field predicate over Fields — the raw material of structured filtering (e.g. category = article, year &gt; 2023, tags containing nlp). |
| `MetadataFilterOperator` | enum | — | Comparison applied by a MetadataFilter. |
| `ParsedQuery` | record | — | The structured form of a raw query: free terms, quoted phrase constraints, expansion atoms, and the combined literal term list a scorer starts from. |
| `QueryExpansion` | record | — | A free-text query atom carrying a search-time expansion operator: the base term (already tokenized to exactly one term) is matched against the index vocabulary… |
| `QueryExpansionKind` | enum | — | How a query atom expands against the index vocabulary. |
| `QueryFeature` | enum | — | The optional LexiSharp query-syntax features a raw query can carry. Engines declare which ones they interpret; using an unsupported one is a hard error rather… |
| `QueryParser` | class | — | Splits a raw query on double quotes before tokenization — the tokenizer treats " as an ordinary separator and would destroy the delimiters — and recognizes the… |
| `QueryRoute` | record | — | The decision an IQueryRouter returns for a query: which route to run, and how confident the router is. |
| `QuerySyntax` | class | — | Detects and validates the LexiSharp query syntax a raw query carries, so an engine that does not interpret a feature fails fast instead of silently treating the… |
| `RawQuerySegments` | record | — | The raw, un-tokenized quote-level split of a query: text outside quotes and the literal interior of each quoted segment. SQL backends forward these strings to… |
| `RerankedTextSearchEngine` | class | `IQueryCostProbe`, `ITextSearchEngine` | A decorator engine that re-ranks the matches of an inner engine before they are returned. |
| `RetrievalAgreement` | enum | — | How much the sources of a federated search agree about one document. |
| `RetrievalAgreementAnalyzer` | class | — | Classifies each document of a federated page by how much its sources agree, from the per-source scores in Contributions. |
| `RetrievalAgreementReport` | record | — | One document's agreement across the sources that produced the page, with the per-source strengths it was derived from. |
| `RetrievalLog` | delegate | — | Sink for the events a RetrievalTelemetry produces. |
| `RetrievalLogEvent` | record struct | — | One observability event emitted by a search engine. Immutable and allocation-free to build, so a sink can consume it without the engine paying more than the… |
| `RetrievalLogEvents` | class | — | Stable event names carried by Event. |
| `RetrievalLogLevel` | enum | — | Severity of a RetrievalLogEvent. |
| `RetrievalMetricsSnapshot` | record | — | A copy of every counter an InMemoryRetrievalMetrics holds. |
| `RetrievalTelemetry` | class | — | The opt-in bundle an engine holds to report what it did: a RetrievalLog sink, an IRetrievalMetrics collector, or both. Pass it to an engine's telemetry… |
| `RoutedEngine` | record | — | A named engine candidate for a RoutedSearchEngine. |
| `RoutedSearchEngine` | class | `IDetailedSearchEngine`, `IExplainableSearchEngine`, `IFacetedSearchEngine`, `IQueryCostProbe`, `ITextSearchEngine` | Opt-in decorator that forwards each query to one of several pre-filled engines, chosen by an IQueryCostEstimator — with CheapestByCandidateCountEstimator the… |
| `RoutingSearchEngine` | class | `ITextSearchEngine` | Opt-in decorator that asks an IQueryRouter which SearchRoute to run each query on, then runs that route's engine with the route's filters merged into the… |
| `ScoreBoost` | record struct | — | A signed score adjustment: a multiplicative factor plus an additive offset. Both can go up or down, so positive and negative boosts are expressed the same way. |
| `ScoreExplanation` | record | — | A transparent breakdown of why a document received its score for a query: global corpus figures, per-term contributions and the scorer's parameter values. |
| `SearchDocument` | record | — | A single unit of text that can be indexed and searched. |
| `SearchOptions` | record | — | Controls how a search is performed and how results are returned. |
| `SearchResult` | record | — | A document ranked against a query. |
| `SearchRoute` | record | — | A named route a RoutingSearchEngine can run: an engine plus optional metadata filters AND-ed onto the caller's own filters when this route is selected. |
| `SearchTrace` | class | — | Collects a TraceStep per document per stage, so a ranking can be explained end to end: which engine scored a document, what each merger contributed, what the… |
| `SparseEmbeddingProviderExtensions` | class | — | Backwards-compatible call shape: embeds text as a Passage. |
| `StageRetrievalMetrics` | record | — | Aggregated measurements for one pipeline stage of one engine. |
| `TermContribution` | record | — | The contribution of a single query term to a document's score, as broken down by an IScoreExplainer. |
| `TextFields` | class | — | Field naming rules shared by the index and the field-aware scorers. |
| `TextIndexStatistics` | record | — | A read-only snapshot of the corpus statistics of an ITextIndex — useful for observability, dashboards and corpus introspection. |
| `TokenEmbeddingProviderExtensions` | class | — | Backwards-compatible call shape: embeds text as a Passage. |
| `TraceStage` | enum | — | The pipeline stage a TraceStep was recorded at. |
| `TraceStep` | record struct | — | One recorded step of a document's journey through the search pipeline: the stage it was at, the score going in, the score coming out, and a stage-specific… |
| `WeightedToken` | record | — | A token paired with a fractional relevance weight, for classifiers that can consume pre-weightened tokens (e.g. a token corrected by a spell-corrector should… |

#### `LexiSharp.Embeddings`

| Type | Kind | Contracts | What it is |
| --- | --- | --- | --- |
| `HashingEmbeddingProvider` | class | `IEmbeddingProvider`, `ITokenEmbeddingProvider` | A deterministic, dependency-free embedding provider built on the hashing trick (feature hashing): every token is hashed to one dimension of a fixed-size vector… |

#### `LexiSharp.Expansion`

| Type | Kind | Contracts | What it is |
| --- | --- | --- | --- |
| `ExpandedTerm` | record struct | — | A single additional term injected into the index for a document, alongside its source text tokenization. The term is indexed as a regular inverted-list member… |
| `ExpandingTextSearchEngine` | class | `IQueryCostProbe`, `ITextSearchEngine` | A decorator engine that widens the query with related terms before delegating to the inner engine — the query-side counterpart of ExpansionTextIndex, which… |
| `ExpansionRanking` | enum | — | Which statistic orders an input term's candidate neighbours before the expansion budget applies. |
| `PmiTermExpander` | class | `ITermExpander` | A corpus-derived ITermExpander: it learns which terms habitually appear in the same context window and expands a document with the top positively associated… |
| `PmiTermExpanderOptions` | class | — | Configuration of a PmiTermExpander: how the corpus is analyzed and how many expansion terms each document gets. |

#### `LexiSharp.Highlighting`

| Type | Kind | Contracts | What it is |
| --- | --- | --- | --- |
| `HighlightOptions` | record | — | Knobs for TextHighlighter. |
| `HighlightSnippet` | record | — | One highlighted excerpt: the source window [Start, Start + Length) with match tags already injected into Text. |
| `TextHighlighter` | class | — | Wraps query matches in the original text: full-string marking for short fields (titles, labels) or padded, word-snapped snippets for long documents. |

#### `LexiSharp.Hybrid`

| Type | Kind | Contracts | What it is |
| --- | --- | --- | --- |
| `CascadeRerankOptions` | record | — | Controls how a CascadeRerankPipeline passes candidates from stage to stage. |
| `CascadeRerankPipeline` | class | `IReranker` | Chains rerankers in cascade: each stage re-ranks the output of the previous one, and the shortlist may be trimmed between stages so only the strongest… |
| `CombMNZResultMerger` | class | `IResultMerger` | Blend strategy that multiplies each document's CombSUM score by the number of engines that retrieved it — the classic CombMNZ. |
| `CombSumResultMerger` | class | `IResultMerger` | Blend strategy that sums the normalized per-engine scores of every document — the classic CombSUM of score-based fusion. |
| `CrossEncoderReranker` | class | `IReranker` | Precision-oriented reranker: re-scores every candidate with a pairwise (cross-encoder) model and reorders the list by the model's judgment. |
| `HybridTextSearchEngine` | class | `IDetailedSearchEngine`, `ITextSearchEngine` | A federated search engine: queries a list of delegate engines, then merges their results into one coherent ordering through an IResultMerger. |
| `MaxSimReranker` | class | `IReranker` | Late-interaction reranker (ColBERT-style): scores each candidate with MaxSim between the token-level embeddings of the query and of the document, then reorders… |
| `MaximalMarginalRelevanceReranker` | class | `IReranker` | Diversity-preserving reranker: greedily re-orders candidates so that each next pick is both relevant to the query and different from what has already been… |
| `ReciprocalRankFusionMerger` | class | `IResultMerger` | Merges ranked lists with Reciprocal Rank Fusion: each document accumulates the reciprocal of its rank (plus a constant k) across every engine that returned it. |
| `RerankingResultMerger` | class | `IResultMerger` | Default merger: rebuilds a single temporary index from the union of candidate documents returned by every engine, then re-scores every candidate with one shared… |
| `VectorSimilarity` | class | — | Pure vector math helpers used to compare embeddings. These are arithmetic utilities, not an embedding pipeline: vectors must already exist (see… |
| `WeightedScoreResultMerger` | class | `IResultMerger` | Blends the native per-engine scores instead of re-ranking. Every engine's scores are normalized to [0, 1] (divided by that engine's maximum positive score),… |

#### `LexiSharp.Indexing`

| Type | Kind | Contracts | What it is |
| --- | --- | --- | --- |
| `AverageLengthDivisor` | enum | — | Which document count divides the corpus token count when AverageDocumentLength is computed. |
| `ContextEnrichingIndex` | class | `ICandidateIndex`, `ITextIndex`, `IVocabularyIndex` | An ITextIndex whose documents first pass through an IChunkContextEnricher: the enriched text is what gets tokenized and scored, while the original document the… |
| `ExpansionTextIndex` | class | `ICandidateIndex`, `ITextIndex`, `IVocabularyIndex` | An InMemoryTextIndex whose documents are additionally indexed under the weak terms returned by an ITermExpander — the « semantic lexical index ». The search… |
| `FieldPrefixContextEnricher` | class | `IChunkContextEnricher` | A model-free IChunkContextEnricher that prefixes a document's text with the values of named Fields — the deterministic, offline cousin of a context-injection… |
| `InMemoryTextIndex` | class | `ICandidateIndex`, `ITextIndex`, `IVocabularyIndex` | In-memory inverted index: for each term, the documents containing it and the positions within each document. Exposes the corpus statistics required by scorers. |
| `InMemoryVectorSearchEngine` | class | `IQueryCostProbe`, `IQuerySyntaxSupport`, `ITextSearchEngine` | In-memory dense (vector) search engine: embeds documents with an IEmbeddingProvider and answers queries by cosine similarity over the whole corpus — a linear… |
| `SparseIndexEntry` | record | — | A document together with its stored sparse vector — the atomic unit of Export and Import, and what a persistence backend serializes to reload a corpus without… |
| `SparseTextSearchEngine` | class | `IQuerySyntaxSupport`, `ITextSearchEngine` | In-memory sparse search engine: indexes documents as their sparse learned embeddings (SPLADE, uniCOIL, ...) and answers queries by dot-product scoring. |
| `VectorIndexEntry` | record | — | A document together with its stored dense embedding — the atomic unit of Export / Import, letting a corpus be reloaded without re-embedding it. |

#### `LexiSharp.Keywords`

| Type | Kind | Contracts | What it is |
| --- | --- | --- | --- |
| `Keyword` | record | — | A keyword extracted from a text, with its extractor-specific relevance score (higher is better; the score scale depends on the extractor). |
| `TextRankKeywordExtractor` | class | `IKeywordExtractor` | Keyword extraction with the graph-based TextRank algorithm: terms are nodes, co-occurring terms within a sliding window are edges, and the PageRank of each node… |
| `TfIdfKeywordExtractor` | class | `IKeywordExtractor` | Keyword extraction by smoothed TF-IDF: a term's score is its frequency inside the text, optionally discounted by how common the term is across a reference… |

#### `LexiSharp.Linguistics`

| Type | Kind | Contracts | What it is |
| --- | --- | --- | --- |
| `PorterStemmer` | class | `IStemmer` | English stemmer implementing Porter's suffix-stripping algorithm: an IStemmer that folds inflected English forms onto a common stem (caresses → caress, motoring… |
| `StopWords` | class | — | Built-in sets of low-information words that can be removed during tokenization. |
| `SynonymMap` | class | — | Raw synonym edges: one-way rewrites (Add) and bidirectional equivalence groups (AddEquivalent). Entries are plain text; the search engine tokenizes them with… |
| `TokenSpan` | record struct | — | A normalized term together with the half-open character range [Start, Start + Length) it occupies in the original text. Offsets are UTF-16 indices (string… |
| `Tokenizer` | class | `ISpanTokenizer`, `ITokenizer` | Splits raw text into normalized terms. |
| `TokenizerOptions` | record | — | Configuration knobs for the Tokenizer. |
| `WordSegmentation` | enum | — | How a character between two word characters affects the word, when WordSegmentation is not Flat. |

#### `LexiSharp.Ranking`

| Type | Kind | Contracts | What it is |
| --- | --- | --- | --- |
| `Bm25Arithmetic` | enum | — | The precision BM25's arithmetic is carried out in. |
| `Bm25FGridPoint` | record | — | One evaluated BM25F configuration and the quality it achieved. |
| `Bm25FParameterTuner` | class | — | Finds the BM25F parameters (k1, b, and a weight per named field) that best satisfy a set of labeled validation queries, by brute-force grid search in two… |
| `Bm25FParameters` | record | — | BM25F tuning knobs: term-frequency saturation (k1), document-length normalization (b), and how much each field counts for. |
| `Bm25FScorer` | class | `IScoreExplainer`, `ITermOverlapScorer`, `ITextScorer` | BM25F ranking: BM25 over a document split into weighted fields, so a term in a title can count for more than the same term in a body. |
| `Bm25FTuningResult` | record | — | The outcome of a Tune run: the best configuration found, the quality it achieved, and the full evaluation grid for inspection. |
| `Bm25GridPoint` | record | — | One evaluated (k1, b) combination and the quality it achieved. |
| `Bm25LGridPoint` | record | — | One evaluated BM25L configuration and the quality it achieved. |
| `Bm25LParameterTuner` | class | — | Finds the BM25L parameters (k1, b and the lower bound delta) that best satisfy a set of labeled validation queries, by brute-force grid search. |
| `Bm25LScorer` | class | `IScoreExplainer`, `ITermOverlapScorer`, `ITextScorer` | BM25L relevance: Okapi BM25 with a compressed term frequency in the numerator and a lower bound on it, which lengthens the reach of rare terms in long… |
| `Bm25LTuningResult` | record | — | The outcome of a Tune run: the best configuration found, the quality it achieved, and the full evaluation grid for inspection. |
| `Bm25ParameterTuner` | class | — | Finds the Okapi BM25 parameters (k1, b) that best satisfy a set of labeled validation queries, by brute-force grid search. |
| `Bm25Parameters` | record | — | Okapi BM25 tuning knobs: term-frequency saturation (k1) and document-length normalization (b). |
| `Bm25PlusGridPoint` | record | — | One evaluated BM25+ configuration and the quality it achieved. |
| `Bm25PlusParameterTuner` | class | — | Finds the BM25+ parameters (k1, b and the lower bound delta) that best satisfy a set of labeled validation queries, by brute-force grid search. |
| `Bm25PlusScorer` | class | `IScoreExplainer`, `ITermOverlapScorer`, `ITextScorer` | BM25+ relevance: Okapi BM25 with the hard term-presence cut replaced by a lower-bounded one, so a document that matches more distinct query terms is rewarded… |
| `Bm25PlusTuningResult` | record | — | The outcome of a Tune run: the best configuration found, the quality it achieved, and the full evaluation grid for inspection. |
| `Bm25Scorer` | class | `IScoreExplainer`, `ITermOverlapScorer`, `ITextScorer` | Okapi BM25 relevance — the standard non-semantic ranking model for full-text search. It blends term saturation (k1) with document-length normalization (b). |
| `Bm25TuningResult` | record | — | The outcome of a Tune run: the best parameter pair found, the quality it achieved and the full evaluation grid for inspection. |
| `Bm25ValidationQuery` | record | — | One labeled validation query of a Bm25ParameterTuner: a raw query together with the ids of the documents a good ranking should surface. |
| `BooleanMatch` | enum | — | How the modifiers of a BooleanScorer are combined. |
| `BooleanScorer` | class | `IScoreExplainer`, `ITermOverlapScorer`, `ITextScorer` | Pure boolean filter. A matching document gets a score of 1, a non-matching one 0. Because the RankedTextSearchEngine discards scores below MinimumScore, this… |
| `NdcgGain` | enum | — | How a relevance level is turned into a gain in NdcgAtK. |
| `ProximityMode` | enum | — | How ProximityReranker turns a term window into a score change. |
| `ProximityReranker` | class | `IReranker` | Re-ranks a candidate list by how close together the query terms actually occur in each document, refining the order a term-frequency scorer produced without… |
| `QueryLikelihoodScorer` | class | `IScoreExplainer`, `ITermOverlapScorer`, `ITextScorer` | Query likelihood retrieval model with Jelinek-Mercer smoothing, a probabilistic language-model alternative to BM25. |
| `QueryTermWeighting` | enum | — | How a scorer treats a term that occurs more than once in a query. |
| `RankedTextSearchEngine` | class | `IExplainableSearchEngine`, `IFacetedSearchEngine`, `IQueryCostProbe`, `IQuerySyntaxSupport`, `ITextSearchEngine` | The stock search engine: delegates the corpus storage to an ITextIndex, delegates the relevance math to an ITextScorer, and takes care of query tokenization,… |
| `RetrievalMetrics` | class | — | Standard top-k retrieval metrics over a ranked list of document ids, used to evaluate (and tune) ranking quality against a validation set. |
| `ScoreConfidence` | class | — | Maps an ordered result set's raw scores into calibrated confidences in [0,1] — the "is this really the answer?" gauge that raw lexical scores (BM25, TF-IDF,… |
| `ScoreConfidenceMethod` | enum | — | How an ordered result set's raw scores are mapped into a calibrated per-result confidence in [0,1]. |
| `TfIdfScorer` | class | `IScoreExplainer`, `ITermOverlapScorer`, `ITextScorer` | Classic TF-IDF relevance. A document matches when it contains at least one query term; rare terms are weighted more than common ones. |
| `TieBreak` | enum | — | How the engines order candidates whose scores are exactly equal. |
| `TuningMetric` | enum | — | The quality metric a Bm25ParameterTuner maximizes over its validation set. |

#### `LexiSharp.Similarity`

| Type | Kind | Contracts | What it is |
| --- | --- | --- | --- |
| `LevenshteinDistance` | class | — | Levenshtein edit distance between two strings: the minimum number of single-character insertions, deletions or substitutions to turn one into the other. |
| `LexicalSimilarity` | class | — | Pairwise lexical similarity measures over short strings — the in-memory answer to "are these two texts roughly the same?": near-duplicate detection,… |

#### `LexiSharp.Sources`

| Type | Kind | Contracts | What it is |
| --- | --- | --- | --- |
| `JsonDocumentLoadOptions` | record | — | Knobs for JsonDocumentsLoader: which object property holds what. |
| `JsonDocumentsLoader` | class | — | Loads documents from a JSON array of objects, e.g. [{"id": "1", "text": "hello"}]. |
| `LoadedDocument` | record | — | A document loaded from an external source (a markdown file, a JSON record, a text file) and ready to be indexed — the bridge between your data and the search… |
| `MarkdownLoadOptions` | record | — | Knobs for MarkdownLoader. |
| `MarkdownLoader` | class | — | Loads markdown documents: the optional YAML front matter (----delimited block at the top of the file) is parsed into structured fields, and the rest of the file… |
| `TextFileLoadOptions` | record | — | Knobs for TextFileLoader. |
| `TextFileLoader` | class | — | Loads plain text files from a directory, whole file content becoming the indexed text. |

### `LexiSharp.MessagePack` package

#### `LexiSharp.MessagePack`

| Type | Kind | Contracts | What it is |
| --- | --- | --- | --- |
| `MessagePackSparseIndexPersistence` | class | — | Saves and reloads a SparseTextSearchEngine as MessagePack binary (LZ4-compressed): the documents themselves — id, text, fields, category — plus the learned… |
| `MessagePackTextIndexPersistence` | class | — | Saves and reloads an InMemoryTextIndex as MessagePack binary (LZ4-compressed): the documents themselves — id, text, fields, category — plus the tokenizer… |

### `LexiSharp.Postgres` package

#### `LexiSharp.ParadeDB`

| Type | Kind | Contracts | What it is |
| --- | --- | --- | --- |
| `ParadeDBOptions` | record | — | Naming and behavior options for the ParadeDB-backed engine. |
| `ParadeDBTextSearchEngine` | class | `IDisposable`, `IQuerySyntaxSupport`, `ITextSearchEngine` | ParadeDB/PG_search-backed ITextSearchEngine: true BM25 ranking built on the Tantivy index of the pg_search extension. The search field is matched with the… |

#### `LexiSharp.Postgres`

| Type | Kind | Contracts | What it is |
| --- | --- | --- | --- |
| `PostgresFuzzyOptions` | record | — | Naming and behavior options for the PostgresFuzzySearchEngine. |
| `PostgresFuzzySearchEngine` | class | `IDisposable`, `IQuerySyntaxSupport`, `ITextSearchEngine` | PostgreSQL-backed ITextSearchEngine for approximate, typo-tolerant matching on top of the pg_trgm extension (trigram similarity) and fuzzystrmatch (levenshtein… |
| `PostgresIndexOptions` | record | — | Naming and behavior options for the PostgreSQL-backed index. |
| `PostgresSchema` | class | — | Shared DDL for the documents table. Used by the lexical (tsvector), vector (pgvector ANN) and ParadeDB (pg_search BM25) engines so they can all point at the… |
| `PostgresSparseOptions` | record | — | Naming, vocabulary and behavior options for the pgvector sparsevec index. The table layout is shared with the lexical engine so both engines can serve a single… |
| `PostgresSparseSearchEngine` | class | `IDisposable`, `IQuerySyntaxSupport`, `ITextSearchEngine` | PostgreSQL-backed learned-sparse search engine (pgvector sparsevec + HNSW): stores each document's learned sparse vector (SPLADE-style term weights) in the… |
| `PostgresTextSearchEngine` | class | `IDisposable`, `IQuerySyntaxSupport`, `ITextSearchEngine` | PostgreSQL-backed ITextSearchEngine: lexical full-text search over a tsvector column with a GIN index, queried with websearch_to_tsquery and ranked with… |
| `PostgresVectorOptions` | record | — | Naming and behavior options for the embeddings-backed (pgvector) index. The table layout is shared with the lexical engine so both engines can serve a single… |
| `PostgresVectorSearchEngine` | class | `IDetailedSearchEngine`, `IDisposable`, `IListableSearchEngine`, `IQuerySyntaxSupport`, `ITextSearchEngine` | PostgreSQL-backed vector search engine (pgvector ANN): stores document embeddings in a shared documents table and answers queries with an approximate… |
| `SparseDistance` | enum | — | Distance operator driving the sparse ANN index and the query ordering. |
| `TrgmIndexKind` | enum | — | Which trigram index structures are created on the content field. |
| `TrgmSearchMode` | enum | — | The way approximate (trigram) search matches documents. |
| `VectorDistance` | enum | — | Distance operator driving the ANN index and the query ordering. |
| `VectorIndexMethod` | enum | — | Index access method used for the ANN index over the embedding column. |

### `LexiSharp.AspNetCore` package

#### `LexiSharp.AspNetCore`

| Type | Kind | Contracts | What it is |
| --- | --- | --- | --- |
| `LexiSharpIndexHealthCheck` | class | `IHealthCheck` | Reports whether a search index is ready to serve traffic: non-empty, and optionally connected to its backing store and not too stale. |
| `LexiSharpIndexHealthCheckOptions` | class | — | Tuning for the registration helpers on LexiSharpObservabilityServiceCollectionExtensions. |
| `LexiSharpObservabilityServiceCollectionExtensions` | class | — | One-call registration of the LexiSharp observability pieces on an IServiceCollection. |
| `LexiSharpSearchEndpoint` | class | — | The testable heart of the search endpoint: an HTTP-agnostic function from an index and request parameters to an IResult. The minimal-API extension only wires… |
| `LexiSharpSearchEndpointBuilder` | class | — | Minimal-API wiring for the LexiSharpSearchEndpoint handler. |
| `LexiSharpSearchError` | record | — | The payload of a failed search response. |
| `LexiSharpSearchHit<TDocument>` | record | — | One hit of a LexiSharpSearchResponse. |
| `LexiSharpSearchParameters` | record | — | Query-string parameters of a LexiSharpSearchEndpoint request, bound by ASP.NET Core model binding. Mirror the knobs of LexiSharpQueryOptions. |
| `LexiSharpSearchResponse<TDocument>` | record | — | The payload of a successful search response. |
| `RetrievalLogger` | class | — | Bridges a RetrievalTelemetry onto ILogger, so an application that already has a logging pipeline gets retrieval events in it without the LexiSharp core taking a… |

