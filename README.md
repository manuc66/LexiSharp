# LexiSharp

[![CI](https://github.com/manuc66/lexisharp/actions/workflows/ci.yml/badge.svg)](https://github.com/manuc66/lexisharp/actions/workflows/ci.yml)
[![CodeQL](https://github.com/manuc66/lexisharp/actions/workflows/codeql.yml/badge.svg)](https://github.com/manuc66/lexisharp/actions/workflows/codeql.yml)
[![codecov](https://codecov.io/gh/manuc66/lexisharp/graph/badge.svg)](https://codecov.io/gh/manuc66/lexisharp)
[![SonarCloud quality gate](https://img.shields.io/sonar/quality_gate/manuc66_lexisharp?server=https://sonarcloud.io)](https://sonarcloud.io/summary/new_code?id=manuc66_lexisharp)
[![NuGet](https://img.shields.io/nuget/v/LexiSharp.svg)](https://www.nuget.org/packages/LexiSharp/)
[![Docs](https://img.shields.io/badge/docs-manuc66.github.io/LexiSharp-blue)](https://manuc66.github.io/LexiSharp/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

> A composable information retrieval toolkit for .NET — build, measure and inspect search
> pipelines, from lexical BM25 to hybrid and reranked retrieval.

Index, retrieve, rank and **judge** a search pipeline: an in-memory inverted index, four
ranking strategies and their BM25 variants, rank fusion, reranking, optional PostgreSQL
backends, and **model-agnostic seams** for dense, learned-sparse and neural scoring — the
models stay in your application. The core package references **no NuGet package at all**.

📖 **[Full documentation →](https://manuc66.github.io/LexiSharp/)** — the guide, the
reference, and every measurement with the command that reproduces it. Published from
[`docs/`](docs/); this README is the short version and the details are delegated to those
pages.

## Install

```bash
dotnet add package LexiSharp   # the core: index, scorers, engines, decorators — no dependencies
```

`net10.0`, MIT. Optional: `LexiSharp.MessagePack` (binary index persistence),
`LexiSharp.AspNetCore` (a `GET /search` minimal-API endpoint), `LexiSharp.Postgres`
(lexical, vector, sparse, fuzzy and true BM25 backends) — [packages](docs/reference.md#packages).

## Use it

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

foreach (var result in engine.Search("textual search"))
    Console.WriteLine($"{result.DocumentId} - {result.Score:0.###}: {result.Document.Text}");
```

`LexiSharpIndex<T>` is the typed facade over the same engine if you would rather hand it your
own objects — [Getting started](docs/getting-started.md).

## See it running

```bash
dotnet run --project samples/LexiSharp.Demo    # → http://localhost:5000
```

Five retrieval strategies over one corpus, compared live — BM25, corpus-derived semantic
expansion, dense hashing embeddings, RRF fusion and a term-overlap rerank — with per-lane
latency, highlighting and a click-through "why did this rank here?" panel. No model, no
external service.

![The demo comparing five retrieval strategies over one corpus — BM25, PMI expansion, hashing embeddings, RRF fusion and a term-overlap rerank, with per-lane latency and highlighting](https://raw.githubusercontent.com/manuc66/LexiSharp/main/docs/images/demo.png)

## What it does not do

Stated plainly, so nothing is implied. The full list, with the measurement behind each claim,
is [Scope and limits](docs/reference.md#scope-and-limits).

- **It does not beat a published BM25.** On three public BEIR corpora the plain BM25 scorer
  reaches nDCG@10 **0.308** (NFCorpus), **0.662** (SciFact) and **0.289** (ArguAna) against
  BEIR's published **0.325 / 0.665 / 0.315** — 5.2 %, 0.5 % and 8.3 % below. Corpora are
  md5-verified on download ([evaluation](docs/evaluation.md)).
- **No scorer here has a measured win over a tuned BM25.** BM25+ and BM25L, tuned on their own
  `δ`, **tie** a tuned BM25 on the reference corpus and NFCorpus and edge it by 0.002–0.004 on
  SciFact — an in-sample margin, so an upper bound rather than a result. On ArguAna, untuned,
  they lose, and no `δ`-tuned ArguAna row exists, so whether tuning closes that gap is
  **unmeasured** ([ranking](docs/ranking.md#bm25-variants)).
- **The SQL backends' retrieval quality is unmeasured.** The BEIR numbers come from the
  in-memory engines; the live integration tests cover schema, query paths and cosine
  behaviour, not relevance ([backends](docs/backends.md)).
- **Not every combination is tested.** Engines, scorers, rerankers and mergers are tested
  individually *and* in the combinations described, but not every pairing — treat an unusual
  one as supported but unproven until you test it on your data.
- **Version 0.5.0, one maintainer.** The public API may still change between minor versions —
  pin a version and read the release notes.

## Development

```bash
dotnet build LexiSharp.slnx
dotnet test  tests/LexiSharp.Tests   # xUnit suite; the Postgres suites need POSTGRES_TEST_CONNECTION
```

Benchmarks, the evaluation harness and the behavioural gate:
[Reference](docs/reference.md#building--testing) and [Benchmarks](docs/benchmarks.md).

## License

MIT — see [LICENSE](LICENSE). The ParadeDB `pg_search` extension used by the BM25 backend is
licensed separately, under AGPL-3.
