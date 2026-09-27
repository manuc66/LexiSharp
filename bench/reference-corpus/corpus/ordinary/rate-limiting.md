---
title: Rate limiting a search endpoint
category: platform
---

A search endpoint is cheap per call and expensive per burst, so the limit belongs on the burst rather than on the average. Limit by caller rather than by address, and return the limit in the header so a client can back off without guessing.
