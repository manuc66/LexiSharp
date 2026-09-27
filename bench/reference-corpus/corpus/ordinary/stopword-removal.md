---
title: When stop words help and when they hurt
category: ingest
---

Removing stop words shrinks the index and sharpens the term statistics. It also breaks phrases, because the words a phrase query needs are exactly the ones that were dropped. A corpus with strong co-occurrence structure, such as this one, needs the removals.
