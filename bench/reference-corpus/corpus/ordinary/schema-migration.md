---
title: Migrating a search schema
category: platform
---

Adding a field to the index is a migration, not a deploy. The old index cannot answer queries that filter on the new field, so either both run for a window or the field is backfilled before the code that reads it ships.
