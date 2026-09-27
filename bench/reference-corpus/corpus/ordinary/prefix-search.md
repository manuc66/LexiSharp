---
title: Prefix search for typeahead
category: search
---

A prefix query expands against the vocabulary, not the documents, so its cost is bounded by the vocabulary rather than the corpus. Capping the expansion keeps a one letter prefix from pulling in the whole term list and pushing the ranking work back up to corpus scale.
