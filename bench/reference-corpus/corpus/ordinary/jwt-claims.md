---
title: Choosing JWT claims
category: auth
---

A claim that is not needed downstream is a liability. Keep the subject, the audience and the expiry; drop the roles array if the resource server can fetch them. A large token costs bandwidth on every request and confuses anyone reading a log line.
