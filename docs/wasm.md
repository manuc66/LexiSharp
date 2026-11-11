---
title: In-browser demo (WASM)
nav_order: 16
# The BCL-only demo is deployed at /wasm/ by .github/workflows/docs.yml. This
# page embeds it, so it cannot live at the same path: `permalink: pretty` would
# otherwise give the page /wasm/ from its filename and the copy would overwrite
# it (or, before the workflow was fixed, fail to write there at all).
permalink: /wasm-demo/
description: >-
  LexiSharp's BCL-only core compiled to WebAssembly: BM25 search runs entirely
  in the browser, with no server and no uploaded documents.
---

# In-browser demo (WASM)

The core of LexiSharp is BCL-only, so it compiles to WebAssembly. This page hosts a small
proof: an in-memory inverted index, BM25 and a cosine comparison run **entirely in the
browser**, with no search API and no document leaving the tab. The corpus and the document
vectors are static files, and the page says which is which.

[Open the in-browser demo]({{ site.baseurl }}/wasm/){: .btn .btn-primary }

<iframe
  src="{{ site.baseurl }}/wasm/"
  title="LexiSharp in-browser search"
  style="width:100%; height:760px; border:1px solid #d6dbe3; border-radius:12px; margin-top:16px;"
  loading="lazy"></iframe>

## What it covers

- `InMemoryTextIndex` and `RankedTextSearchEngine`
- BM25 scoring
- the demo's 26-document corpus, fetched once from a static JSON file and indexed in the tab
- "similar documents" over precomputed embeddings, ranked by cosine in the tab
- `Explain` — the engine's own term-by-term BM25 breakdown behind "why" on every result

## What is precomputed, and what is not

Two files are fetched once and then used locally:

| File | Produced by | Runs in the tab |
|---|---|---|
| `corpus.json` | `samples/LexiSharp.Demo/corpus.json` — embedded by the server demo, copied to `wwwroot` for the browser | BM25 indexing and search |
| `embeddings.json` | `scripts/generate-embeddings.py`, with **BAAI/bge-small-en-v1.5** (MIT, 384 dimensions) | loading the vectors and computing cosine |

The embedding model is **not** downloaded, loaded or executed by the page. The vectors were
computed offline, once, and the "similar" panel names the model next to the results. That is
the honest boundary: BM25 works for any query, while "similar documents" is limited to the
26 documents whose vectors were generated.

## What it does not cover

- the segment store (it needs a writable file system)
- PostgreSQL / ParadeDB backends
- ONNX Runtime
- embedding a new document or a free-text query: no model runs in the tab
- ASP.NET Core hosting
- the classification and evaluation panels of the server demo

The server demo in `samples/LexiSharp.Demo` remains the full showcase.
