---
title: Mapping a match back to the source
category: search
---

Highlighting works on normalized text, so the offsets a tokenizer reports no longer line up with the original string once case or accents differ. Tokenizing the query with the same configuration the index used is what makes the marks land on the right words.
