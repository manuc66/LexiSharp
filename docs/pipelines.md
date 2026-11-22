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

### Normalizing the scores before a blend

A score blend (`WeightedScoreResultMerger`, `CombSumResultMerger`, `CombMNZResultMerger`) has to put
one engine's scores on the other's scale first, and that mapping is a choice, not a detail. It is
`IScoreNormalizer`, and the two mergers that consume it take one in their constructor:

```csharp
IResultMerger blend = new WeightedScoreResultMerger(
    new MinMaxScoreNormalizer(),
    weights: [1.0, 1.0]);
```

| Normalizer | Mapping | Unit set by | Costs |
| --- | --- | --- | --- |
| `MaxScoreNormalizer` (default) | `s / max(s)` | the single best score | everything else is a fraction of one document |
| `MinMaxScoreNormalizer` | `(s − min(s)) / (max(s) − min(s))` | both ends of the list | a tightly packed list is stretched; the last document of each list contributes `0` |
| `SumScoreNormalizer` | `s / Σs` | the whole list (L1) | the unit depends on the pool depth the search returned |
| `UpperBoundScoreNormalizer` | `s / MaxScore(Q)` | the query's theoretical maximum, when the engine reports it | falls back to `max` for a query or scorer without a bound |

The first three are stateless and read the list they are given — one engine's candidates for one
query — so none of them needs a calibration set. The default is `MaxScoreNormalizer`, which is the
behaviour the mergers had before the seam existed: passing none changes no score.

**The measurement, and why the default did not change.** nDCG@10, `--no-tuned`, `Hybrid BM25+QL`,
weights 1/1, `--hybrid-normalizer minmax,sum`:

| Corpus | max | minmax | sum |
|---|---:|---:|---:|
| ArguAna (1 406 q) | **0.320** | 0.320 | 0.320 |
| FiQA-2018 (648 q) | **0.232** | 0.232 | 0.232 |
| NFCorpus (323 q) | **0.308** | 0.299 | 0.308 |
| SciFact (300 q) | **0.662** | 0.662 | 0.662 |

**No normalizer beats the default, and min-max loses 0.009 on NFCorpus.** The honest reading is
narrower than the table: on these four corpora the BM25+QL blend reads exactly BM25's score — the
query-likelihood half is weaker than BM25 on all of them (`docs/ranking.md` measures it) — so the
blend has almost no room to be changed by its normalization, and this run cannot rank the three
strategies. What it does establish is that changing the default is not justified by evidence, which
is why `max` stays the default and the other two are opt-in. The rows to settle the question are
hybrids whose two halves are genuinely complementary (lexical + dense, or lexical + learned
sparse); `--hybrid-normalizer` measures those the same way.

### Normalising by the query's upper bound

`UpperBoundScoreNormalizer` divides a lexical engine's scores by the largest score any document
*could* reach for the query, instead of by the largest score the page happened to return. The bound
comes from the scorer: for BM25, each term contributes at most

```
idf(t) · maxTf(t) · (k1 + 1) / (maxTf(t) + k1 · (1 − b + b · minLen(t) / avgdl))
```

where `maxTf(t)` is the largest term frequency in one document carrying `t`, and `minLen(t)` is the
smallest token count among those documents. Both statistics are stored by the segment dictionary and
built with the in-memory posting copy, so asking for them costs no posting walk.

```csharp
IResultMerger blend = new WeightedScoreResultMerger(
    new UpperBoundScoreNormalizer(),
    weights: [1.0, 1.0]);
```

`CombSumResultMerger` and `CombMNZResultMerger` accept it too; `RerankingResultMerger` and
`ReciprocalRankFusionMerger` re-score or read ranks, so there is no per-engine scale to normalize
and they need no context.

It is opt-in, and it falls back to `MaxScoreNormalizer` whenever it cannot answer — a phrase,
prefix, fuzzy, required/prohibited or field-scoped query, an empty corpus, a scorer that does not
implement `IScoreBoundScorer` (`BM25`, `BM25+`, `BM25L` and TF-IDF do; `BM25F`, `BM25VA`,
query-likelihood and boolean do not), or an index that cannot report its posting statistics. A
decorator that rebuilds the scorer per query — `ReweightingTextSearchEngine`, for one — does not
forward the capability today, so its searches fall back even though the scorer underneath could
bound them. A fallback is per query, so a run can contain both mappings; the trace names the
normalizer, not the per-query choice.

**The bound is valid but not tight.** It sums each term's independent maximum, so it bounds a
document that achieved every term's best value at once; no such document need exist. A loose bound
scales a query's scores down as a whole, which leaves the ranking of one engine unchanged and is
absorbed by the fusion weight.

**The measurement, on the lane where the two scales are genuinely different.** nDCG@10,
`--no-tuned`, `--dense` (multilingual-e5-small), weights 1/1, paired bootstrap over queries with
10,000 resamples and a Holm correction across the configurations; baseline is the default
`max` blend:

| Corpus | max | upper-bound | Δ [95 %] | p (Holm) | minmax Δ [95 %] (p) |
|---|---:|---:|---|---:|---|
| SciFact (300 q) | 0.6810 | **0.6939** | **+0.0130** [+0.0064, +0.0207] | 0.0002 (0.0036) | +0.0217 [+0.0016, +0.0418] (0.3113) |
| NFCorpus (323 q) | **0.3233** | 0.3227 | −0.0006 [−0.0041, +0.0027] | 0.7348 (0.7348) | **+0.0112** [+0.0027, +0.0200] (0.0339) |

**Upper-bound's SciFact gain survives the correction, and its NFCorpus result is neutral**
(−0.0006, interval crossing zero). Min-max has the larger point estimate on both corpora, but its
SciFact interval includes zero; its NFCorpus gain is the one that survives. On the earlier
BM25 + query-likelihood lane min-max lost 0.009 on NFCorpus. That is why `max` stays the default and
both alternatives are opt-in: two corpora and one dense model establish a direction, not a
replacement.

```bash
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset scifact --dense --no-tuned \
  --hybrid-normalizer max,minmax,sum,upper-bound \
  --bootstrap --bootstrap-baseline "Hybrid BM25+Dense weighted"
```

The per-engine bound is also reachable directly: `RankedTextSearchEngine.TryGetQueryBound` returns
the total and the per-term figures, for a caller that wants to normalise outside a hybrid or to see
which term made the bound loose. `UpperBoundScoreNormalizer` and
`IContextualScoreNormalizer` are covered in the [API reference](api.md).

How tight that bound is, measured rather than assumed: the [bound-tightness
lane](benchmarks.md#bound-tightness-lane-bound-tightness-how-much-of-the-upper-bound-a-query-reaches)
reads a median of **72.8%** of the bound on NFCorpus BM25 and **43.2%** on SciFact, and a
deliberately wide 50-term query reaches only **32–36%**. A loose bound rescales a query as a whole,
which is why the normalizer's measured SciFact gain is +0.0130 and not more, and why `max` stays
the default.

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

## Whether to route, measured

The fusion is worth having where the lexical lane fails. Over the queries where BM25 scores zero,
RRF scores **+0.116** nDCG@10 on average across 60 SciFact queries and **+0.025** across 104 on
NFCorpus, and **no query was made worse** in either set — 164 queries, 39 improved, none degraded.

That makes a router attractive: run the dense lane only where it pays. Three candidate signals were
measured against that, and each fails in its own way.

| signal | what it reads | result |
|---|---|---|
| idf coverage | how much of the query the corpus knows | the rule fitted on it — fuse below a threshold — depends on the judgments, and the two corpora do not put that threshold in the same place |
| out-of-vocabulary terms | query terms the index has never seen | keeps **72 %** of the always-fuse gain on SciFact, for about 0.2 ms a query |
| top-10 disagreement | Jaccard distance between the two lanes' pages | rank correlation with the fusion gain **0.006** on NFCorpus and **−0.077** on SciFact; with the rerank gain **0.075**, on a 100-query SciFact prefix |

The disagreement is the one that needs no labels, and it fails for a structural reason: it needs both
rankings, so it cannot decide whether to run the encoder — only whether to run a stage after both
have run. It does separate the queries where the lexical lane returns nothing, weakly: at a distance
of 0.9, 61 % of the NFCorpus queries selected are zeros against a 32 % base, with 70 % of the zeros
captured; on SciFact 41 % against 20 %, with 40 % captured. The distribution also sits near the top
of its range — no query on either corpus has identical top-10 pages, 66 of 323 and 13 of 300 share no
id at all, and 212 of 323 and 184 of 300 are above 0.8 — so a threshold there cuts the corpus rather
than the hard cases.

Two things frame those numbers. Missing vocabulary is not what makes a query hard: of the 104
NFCorpus queries where BM25 scores zero, **67 have every term in the index**, so the difficulty is in
the ranking rather than in a gap a thesaurus could fill. And the rerank row is a 100-id prefix rather
than a random sample; the full 300-query rerank run did not finish in the session that measured it.

Nothing here routes by default: `HybridTextSearchEngine` queries every lane, and both routers below
take a decision the caller supplies. What would reopen the question is a signal deeper than the top
ten, a rank-weighted distance, or one learned across corpora rather than a threshold rule. The
harness measures it per corpus and writes one row per query, so every subset above is a filter over
its output:

```bash
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset nfcorpus --dense --no-tuned \
  --analyze /tmp/nf.tsv --analyze-baseline "BM25 (k1=1.5, b=0.75)" \
  --analyze-candidate "Hybrid BM25+Dense RRF" \
  --analyze-disagreement-left "BM25 (k1=1.5, b=0.75)" \
  --analyze-disagreement-right "Dense multilingual-e5-small"
```

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
