---
title: Connection pooling
category: platform
---

A pool sized to the number of concurrent requests starves under burst load and wastes memory when idle. Size it from the database's own connection limit divided by the number of instances, and leave headroom for the maintenance job that also needs one.
