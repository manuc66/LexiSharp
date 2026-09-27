---
title: Zertifikatrotation
category: auth
tags: [auth, zertifikate, Rotation]
---
Ein Zertifikat läuft nach dreißig Tagen ab. Die Rotation erfolgt automatisch:
der Client fordert ein neues Zertifikat an, bevor das alte widerrufen wird. Bleibt
die Rotation aus, scheitert die Prüfung des Aufrufers.
