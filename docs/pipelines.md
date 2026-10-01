---
title: Rerankers and hybrid search
nav_order: 6
description: >-
  Rerankers (MMR, cascade, cross-encoder, MaxSim), hybrid federation across
  engines, and cost-based or intent-based query routing.
---

# Rerankers and hybrid search

Every engine here implements the same `ITextSearchEngine` contract, and they compose by
wrapping rather than by configuring: a first stage retrieves for recall, a second stage
re-orders a shortlist for precision, and a merger or a router decides where a query goes.

- **Retrieve, then rerank** — `RerankedTextSearchEngine` over-fetches, re-ranks, and
  applies `MinimumScore`/`Limit` to the final scores. Shipped rerankers: a
  diversity-preserving **MMR**, a **cascade** that chains any number of stages, a
  **cross-encoder** over a consumer-provided `ICrossEncoderScorer`, **ColBERT MaxSim** over
  `ITokenEmbeddingProvider`, and [proximity](ranking.md#proximity).
- **Federate** — `HybridTextSearchEngine` queries several engines, de-duplicates the
  candidates by document id and merges them with one of five strategies, from a
  re-scoring merger to reciprocal-rank fusion for scores that are not comparable.
- **Route** — `RoutedSearchEngine` forwards each query to the cheapest engine;
  `RoutingSearchEngine` asks a decision you supply which pre-composed route to run, with a
  confidence threshold and a fallback.

The models stay in your application: `IEmbeddingProvider`, `ISparseEmbeddingProvider`,
`ITokenEmbeddingProvider` and `ICrossEncoderScorer` are seams, and LexiSharp never runs
one. The [`HashingEmbeddingProvider`](embeddings.md#in-memory-dense-retrieval-inmemoryvectorsearchengine-hashingembeddingprovider)
makes the whole stack testable offline, with no model at all.

## Reranking (`IReranker`, MMR, cascade)

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

## Hybrid engine

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
| `CombSumResultMerger` | sum of normalized per-engine scores | scores (not just ranks) are meaningful and should add up |
| `CombMNZResultMerger` | CombSUM × number of engines that returned the doc | reward cross-engine **agreement** |

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

## Cost-based routing (`RoutedSearchEngine`)

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
wrapper does not currently surface facets/detailed/explain, whose contracts it cannot
preserve.

## Intent-based routing (`RoutingSearchEngine`)

Cost routing picks the cheapest engine; intent routing picks the *right* one. Route each query to
a pre-composed target — an engine plus optional metadata filters — chosen by a decision you
supply (a rule, a small classifier, or a model behind `IQueryRouter`):

```csharp
using LexiSharp.Core;

var router = new RoutingSearchEngine(
    new KeywordVsQuestionRouter(),                 // your IQueryRouter
    new[]
    {
        new SearchRoute("keywords", memoryEngine),
        new SearchRoute("questions", pgEngine, new[]
        {
            new MetadataFilter("kind", MetadataFilterOperator.Equal, "faq"),
        }),
    },
    fallbackId: "keywords",
    minimumConfidence: 0.6);

IReadOnlyList<SearchResult> hits = router.Search("how do I reset my password?");
```

The router only chooses among the ids it is given (`RouteAsync(query, candidateIds)`); it never
builds filters or touches indexes. The selected route's filters are AND-ed onto the caller's
`SearchOptions.Filters`. A `null` decision, an unknown id, a confidence below the threshold or a
thrown exception all run the fallback route — a broken router never breaks a search. The seam is
async (`ValueTask<QueryRoute?>`) because a model-backed router is naturally async, but the
synchronous `Search` blocks on it, like the PostgreSQL engines block on `IEmbeddingProvider`.
LexiSharp never runs a model itself: you provide the rule, classifier or model.

## Query transformation (pre-retrieval)

Users write bad queries; the seam for fixing them before they touch the index is `IQueryTransformer`.
It rewrites the raw query into one or more **variants** — a rewrite, the sub-queries of a decomposed
question, lexical variants, or a HyDE-style hypothetical answer — and `TransformingTextSearchEngine`
searches every variant against the same inner engine and fuses the rankings:

```csharp
using LexiSharp.Core;
using LexiSharp.Expansion;

ITermExpander expander = PmiTermExpander.LearnFrom(corpus);
IQueryTransformer transformer = new ExpansionQueryTransformer(expander); // model-free reference

ITextSearchEngine engine = new TransformingTextSearchEngine(baseEngine, transformer);
IReadOnlyList<SearchResult> hits = engine.Search("how do I renew an expired token?");
```

Every variant runs on the **same** engine and scorer, so one scale compares them all: the fusion
keeps each document's best score across the variants, then cuts the page (`Offset`/`Limit`) from
the fused ranking — the window logic of the hybrid engine, applied to query variants. The reference
implementation returns the query *and* a widened variant (expansion terms appended), which is what
`ExpandingTextSearchEngine` does as one query, fused instead so a document matching the caller's
exact terms keeps that tight score rather than being diluted by the expansion terms.

**A broken transformer never breaks a search** — the router rule again: null/empty/blank variants,
or a `Transform` that throws, fall back to the untransformed query. Variants are deduplicated.
The model is consumer code, as everywhere: a HyDE generator is `IQueryTransformer` over an LLM call,
and LexiSharp never runs it. Writes forward unchanged. The decorator does not offer facets, detailed
results or explanations, whose per-document contracts do not survive a multi-variant fusion.

```csharp
var index = new LexiSharpIndex<SearchDocument>(o =>
{
    o.QueryTransformer = myHydeGenerator;   // IQueryTransformer, consumer-provided
    o.UseBm25();
});
```

## Structure-aware retrieval

Documents from the real world live in trees — a chunk under a section, a section under a chapter,
a chapter under a book — and flat retrieval loses that. `DocumentHierarchy` is the structure, and
`HierarchicalTextSearchEngine` the navigation surface on top of a flat engine:

```csharp
using LexiSharp.Core;

var tree = DocumentHierarchy.FromChildToParent(new Dictionary<string, string>
{
    ["chunk-17"] = "sec-3",
    ["sec-3"] = "ch-2",
    ["ch-2"] = "book-1",
});

var engine = new HierarchicalTextSearchEngine(
    baseEngine,                       // any ITextSearchEngine — flat retrieval unchanged
    tree,
    id => ledger.GetValueOrDefault(id)); // your document store, for resolving ancestors
```

Searching still finds the precise node — the ranking is untouched. What the wrapper adds is the
second surface: `engine is IStructureAwareSearchEngine`, and on it `GetAncestorIds(hitId)` walk
from the hit's leaf up toward the overview (`sec-3`, `ch-2`, `book-1`) and `GetAncestors(hitId)`
resolve those ids to documents through the ledger you supplied, omitting any the ledger cannot
produce. Descendants go the other way (`sec-3` → `chunk-17`), breadth-first in declared order,
for an "overview to precise nodes" drill-down.

The hierarchy is app-declared over the units you already index — the library neither chunks
documents nor imposes a shape. An id the hierarchy does not know behaves like a root. Building a
hierarchy validates it and names the defect: self-parenting, a node with two parents, or a cycle
are each refused at construction.

## Graph-augmented retrieval (GraphRAG)

Text and vectors answer "which documents say this"; a knowledge graph answers "what is
connected to this". `IKnowledgeGraphBridge` is the seam to the second kind of store — relation
extraction is your model, the graph (Apache AGE, Neo4j, ...) is your store, and LexiSharp
runs neither. The contract is two calls with one vocabulary: `ExtractRelationsAsync` mines
subject-verb-object triplets from a text (a document in your ETL, or the query at search
time), and `QuerySubGraphAsync` reads the neighbourhood around a set of entities.

```csharp
using LexiSharp.Core;

IKnowledgeGraphBridge bridge = myAgeBridge; // consumer-provided: extractor + graph store

var engine = new GraphAwareSearchEngine(baseEngine, bridge, maxHops: 2);
```

Searching and writing are forwarded unchanged — the ranking is untouched. The graph is an
opt-in surface, detected like the other capabilities:

```csharp
if (engine is IGraphSearchEngine graphEngine)
{
    GraphHydratedResults hydrated = graphEngine.SearchWithGraphFacts("which pumps failed after the upgrade");
    // hydrated.Results is exactly Search(...)'s page;
    // hydrated.Facts carries the triplets connected to the query's mined entities.
}
```

The query's entities are mined with the bridge's own extractor, so the query is asked in the
vocabulary the graph was fed. A query that mines no entities skips the graph and carries an
empty context. The flat `Search` never touches the bridge, so a graph outage degrades nothing
by default; the facts call that asks for the graph propagates its failure. Persisting the
triplets extraction returns is the implementation's business (your ETL), not the decorator's.

## Context compression for LLM generation

The page that goes to a generation model should carry the passages that answer the query, not
the whole documents. `IContextCompressor` is the seam, `CompressingTextSearchEngine` the
opt-in surface, and `QueryWindowCompressor` the model-free reference — windows of words around
each query-term occurrence, merged and joined:

```csharp
using LexiSharp.Compression;
using LexiSharp.Core;

IContextCompressor compressor = new QueryWindowCompressor(windowRadius: 16); // or your learned one

var engine = new CompressingTextSearchEngine(baseEngine, compressor);

if (engine is ICompressingSearchEngine compressing)
{
    IReadOnlyList<CompressedHit> lean = compressing.SearchCompressed("benefit rose 12%");
    // same ranking (ids and scores) as Search(...); each hit's text is the query-relevant
    // content, and CompressionRatio says how much of the source it retains.
}
```

The contract returns text only; the engine measures the ratio once (`compressed.Length /
source.Length`, 0 when the source is empty), so every compressor reports the same number. The
flat `Search` still returns the full text — this surface is what a RAG pipeline calls just
before handing the context to the model. Extractive by nature here: a document whose text
contains none of the query terms compresses to nothing, which is the honest reading of "no
query-relevant content". A learned compressor (perplexity-based, LLM rewrite) is a consumer
implementation of the same seam, exactly like the model-free one.

## Putting the stages together

The reranking stage composes with all of it: wrap the hybrid in a
`RerankedTextSearchEngine` (core decorator) and pass a `CascadeRerankPipeline`, a
`MaximalMarginalRelevanceReranker`, a `CrossEncoderReranker` or a `MaxSimReranker` to add a
precision or diversity pass on top of the fused ranking — the same two-stage
retrieve-then-rerank shape, one line of composition.
