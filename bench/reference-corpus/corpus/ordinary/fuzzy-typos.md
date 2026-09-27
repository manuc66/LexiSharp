---
title: Correcting a typo without breaking a word
category: search
---

Edit-distance correction is only safe outside the vocabulary. A word already in the index is spelled correctly by definition, so replacing it with a closer variant can only lose precision. Restricting correction to unknown words removes that failure mode entirely.
