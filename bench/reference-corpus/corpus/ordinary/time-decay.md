---
title: Preferring recent documents
category: ranking
---

A boost that scales a score by the age of a document is a signed adjustment like any other, and it belongs in a decorator rather than in the scorer, because the scorer has no notion of a document having a date.
