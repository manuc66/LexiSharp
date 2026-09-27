---
title: Parsing a query once
category: search
---

A query is parsed once per search, not once per candidate document. The terms, the quoted phrases and the expansion decisions are all resolved before the scoring loop starts, which is what keeps a four term query from costing four times the tokenizer.
