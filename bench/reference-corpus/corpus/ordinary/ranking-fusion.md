---
title: Why fuse by rank instead of by score
category: ranking
---

Two engines rarely agree on what a score means. A cosine near zero point eight and a BM25 near twelve are not comparable, and normalizing them invites an argument about the curve. Rank fusion sidesteps the question entirely by using position, which both engines agree to produce.
