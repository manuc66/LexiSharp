---
title: Rebuilding an inverted index
category: search
---

A rebuild is cheaper than an incremental update once the update rate exceeds a few percent of the corpus per day. Build the replacement beside the live index, then swap the reference, because an in-place rebuild pins the old postings in memory for its whole duration.
