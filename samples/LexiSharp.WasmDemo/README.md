# LexiSharp WASM demo

A minimal Blazor WebAssembly proof that the BCL-only core of LexiSharp runs in the browser:
BM25 indexing and search happen entirely client-side, with no server API.

```bash
dotnet run --project samples/LexiSharp.WasmDemo
```

The page indexes three documents in memory and searches them with `RankedTextSearchEngine`
and `Bm25Scorer`.

## Scope

This proof covers the search path only. The server demo (`samples/LexiSharp.Demo`) also
exercises classification, segment storage, persistence and the optional packages. Browser
constraints mean the following are intentionally absent here:

- PostgreSQL / ParadeDB backends
- ONNX Runtime
- the segment store (it needs a writable file system)
- ASP.NET Core hosting endpoints
