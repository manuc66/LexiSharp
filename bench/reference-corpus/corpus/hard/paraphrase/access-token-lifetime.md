---
title: Access token lifetime
category: auth
tags: [auth, tokens, lifetime]
---
An access token is short lived by design. It carries no state of its own: the
resource server validates the signature and the expiry, then discards it. A
client that wants a longer session holds a refresh token instead, which is the
only thing the authorization server will exchange.
