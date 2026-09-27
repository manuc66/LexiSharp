---
title: Token introspection
category: auth
---

Introspection asks the authorization server whether a token is currently valid instead of validating the signature locally. It costs a round trip per call, so it is used for opaque tokens and for long lived ones, never for a high volume internal path.
