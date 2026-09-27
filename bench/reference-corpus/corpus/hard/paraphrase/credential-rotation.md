---
title: Credential rotation
category: auth
tags: [auth, credentials, rotation]
---
A credential is rotated before it expires, not after. The rotation window is
ninety days; inside it the old credential still authenticates, outside it the
verification step fails closed. Rotation is scheduled, never triggered by a
failed login, because a failed login means the credential already leaked.
