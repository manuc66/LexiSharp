---
title: Loading documents from disk
category: ingest
---

A loader that walks a directory should skip hidden paths and honour a depth limit, because an ingest that follows a symlink loop hangs rather than fails. The document id is the path relative to the scanned root, which keeps it stable across machines.
