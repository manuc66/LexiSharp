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
proof: an in-memory inverted index and BM25 run **entirely in the browser**, with no API
call and no document leaving the tab.

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

## What it does not cover

- the segment store (it needs a writable file system)
- PostgreSQL / ParadeDB backends
- ONNX Runtime
- ASP.NET Core hosting
- the classification and evaluation panels of the server demo

The server demo in `samples/LexiSharp.Demo` remains the full showcase.
