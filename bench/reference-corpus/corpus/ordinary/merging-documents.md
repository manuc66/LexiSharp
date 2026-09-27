---
title: De-duplicating before indexing
category: ingest
---

The same page arrives from two crawlers under two ids. Indexed twice, it occupies two slots in every result page and halves the effective corpus. Deduplicating on a content hash before indexing is cheaper than any downstream fix.
