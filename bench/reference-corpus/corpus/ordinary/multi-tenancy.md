---
title: Isolating tenants in one index
category: platform
---

A metadata filter applied after scoring pays for relevance math on documents the caller was never allowed to see. Applied before, it shrinks the candidate set first. The difference is visible under load, where the unfiltered version spends its time on rows it will discard.
