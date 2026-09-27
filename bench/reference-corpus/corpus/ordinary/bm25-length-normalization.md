---
title: Why length normalization exists
category: ranking
---

A long document accumulates term frequency faster than a short one, so a two hundred word page outranks a precise forty word answer purely by being long. Length normalization divides that growth back out, and the b parameter decides how aggressively: zero ignores length entirely, one treats it fully.
