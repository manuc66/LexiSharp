---
title: Stable ranking
category: search
---

Two documents with the same score must always come back in the same order, or a stored result stops matching a fresh one. Ties therefore break on something fixed, such as the corpus order, and never on a hash seed or a parallel completion order.
