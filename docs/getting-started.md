---
title: Getting started
nav_order: 2
description: >-
  Install LexiSharp and run your first search — the engine in three lines, the
  typed LexiSharpIndex<T> facade, document loaders, and an ASP.NET Core endpoint.
---

# Getting started

Four ways in, from smallest to largest: the three-line engine, the typed facade, loaders
for data on disk, and an HTTP endpoint. The [home page](index.md) has the shortest version
of all, and the demo ([See it running](index.md#see-it-running)) is worth running once before
reading any of this.

Everything on this page is in the core package unless a section says otherwise: the
namespaces `LexiSharp.Sources`, `LexiSharp.Classification` and the rest all ship inside
`LexiSharp`. The separate packages are only `LexiSharp.MessagePack`,
`LexiSharp.AspNetCore` and `LexiSharp.Postgres`.

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

## Drop-in index (`LexiSharpIndex<T>`)

The fastest way in: a typed facade that maps your own documents to the engine and back, so
you go from "a list of objects" to "working search" in a few lines. `TDocument` can be
`string` or `SearchDocument` (selectors default to the obvious mapping), or any class with
explicit id/text selectors:

```csharp
using LexiSharp;

var search = new LexiSharpIndex<MyDocument>(o =>
{
    o.Id = d => d.Id;
    o.Text = d => d.Body;
    o.EnableFuzzy = true;        // plain terms behave like `term~1`
});

search.Add(documents);

var hits = search.Search("architecture distributed systems");
foreach (var hit in hits)
    Console.WriteLine($"{hit.DocumentId} - {hit.Score:0.###}: {hit.Document.Body}");
```

`Search` returns typed hits carrying the original document; `SearchWithFacets` adds facet
buckets over the match set, `Explain` returns the per-term score breakdown, `Statistics` a
corpus snapshot. `Highlight: true` on the query options wraps the matched terms of each hit.
The scorer/tokenizer knobs of the rest of the library stay reachable through
`LexiSharpIndexOptions<T>` (`UseBm25`, `UseTfIdf`, `UseQueryLikelihood`, `UseBoolean`,
`RemoveStopWords`, `Stemmer`, `NGramMax`, `Synonyms`, `Reranker`, ...).

## Document sources (`LexiSharp.Sources`)

Pair the facade with the loaders to index data that lives on disk: each loader produces a
`LoadedDocument` (id + text + optional fields/category) that maps straight into a
`LexiSharpIndex<LoadedDocument>` or a `SearchDocument`.

```csharp
using LexiSharp.Sources;

var notes = MarkdownLoader.LoadDirectory(@"./notes");          // *.md, *.markdown, *.mdx
var ledgers = TextFileLoader.ScanDirectory(@"./ledgers");      // any plain text files
var records = JsonDocumentsLoader.Parse(json, new JsonDocumentLoadOptions
{
    IdProperty = "docid",
    TextProperty = "content",
    CategoryProperty = "bucket",
});
```

- **`MarkdownLoader`** — reads a file (`LoadFile`) or a whole directory (`LoadDirectory`,
  recursive, hidden paths skipped), parses the optional `---` YAML front matter into fields
  (`title`, `category`, `tags` as `[a, b]` lists, plus any other `key: value`), and indexes the
  remaining text. Document ids default to the full path for files and the forward-slash
  relative path for directory scans.
- **`TextFileLoader`** — whole file content is the indexed text; `title` and `source` fields
  are added automatically.
- **`JsonDocumentsLoader`** — parses a JSON array of objects; scalars become fields, scalar
  arrays are flattened to a comma-separated string; the id/text/category property names are
  configurable.

## ASP.NET Core search endpoint (`LexiSharp.AspNetCore`)

A minimal-API endpoint that exposes any registered `LexiSharpIndex<T>` over HTTP — a thin
package built on the core (no web framework of its own; you reference it from your ASP.NET
Core app):

```csharp
using LexiSharp;
using LexiSharp.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

var index = new LexiSharpIndex<SearchDocument>();
index.AddRange(loader.LoadDirectory("data"));
builder.Services.AddSingleton(index); // resolve the same<T> the endpoint uses

var app = builder.Build();
app.MapLexiSharpSearch<SearchDocument>(); // GET /search?q=...&limit=10&offset=0&highlight=true

app.Run();
```

- Query parameters: `q` (required), `limit` (default 10), `offset` (default 0),
  `minimumScore` (default −∞), `highlight` (default false).
- A blank `q` returns `400` with a `{ "field": "q", "message": ... }` error payload.
- With `highlight=true` each hit carries `highlightedText`, the document text with matched
  terms wrapped in `<em>`; the page honors `Limit`/`MinimumScore`/`Offset` exactly like the
  in-process `LexiSharpIndex<T>.Search`.

Change the ranking algorithm without rebuilding the index:

```csharp
var bm25Engine   = new RankedTextSearchEngine(index, new Bm25Scorer());
var tfIdfEngine  = new RankedTextSearchEngine(index, new TfIdfScorer());
var booleanEngine = new RankedTextSearchEngine(index, new BooleanScorer(BooleanMatch.AllTerms));
```

## Reference demo (`samples/LexiSharp.Demo`)

A self-contained ASP.NET Core app that runs **five retrieval strategies over the same corpus**
and compares them live — plain BM25, corpus-derived semantic expansion, dense hashing
embeddings, reciprocal-rank fusion, and a term-overlap rerank — with per-lane latency and
highlighting. Clicking any hit opens a **"why did this rank here?"** panel powered by
`LexiSharpIndex.Explain` (per-term contributions, IDF, length) or, for the federated/dense
lanes, the per-source scores from `HybridTextSearchEngine.SearchWithDetails`. No external model
or dependency: the semantic lane uses `PmiTermExpander`, the dense lane a
`HashingEmbeddingProvider`, and the rerank lane a local term-overlap `ICrossEncoderScorer` —
swap any of them behind its seam for a real model.

```bash
dotnet run --project samples/LexiSharp.Demo
# → http://localhost:5000  (search box + five comparison columns)
```

See [See it running](index.md#see-it-running) for what this buys you and how to read the lanes.
