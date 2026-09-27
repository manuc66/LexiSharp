---
title: OAuth authorization code flow
category: auth
---

The authorization code flow keeps the access token out of the browser. The client receives a short code, exchanges it server side for the token, and never holds a refresh token in a page context. The implicit flow did the opposite and is no longer recommended for anything but a legacy client that cannot be upgraded.
