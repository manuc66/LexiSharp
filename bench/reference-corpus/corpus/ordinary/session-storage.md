---
title: Choosing a session store
category: platform
---

An in-process session store loses every session when the process restarts and cannot be shared between instances. A shared store costs a round trip per request and buys nothing until there is more than one instance. Pick the one you can live with when the second instance appears, and not before.
