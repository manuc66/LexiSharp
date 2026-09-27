---
title: Session renewal, end to end
category: auth
tags: [auth, session, renewal]
---

# Session renewal, end to end

Renewal is the least eventful part of a session and the most common source of
support tickets, which is why it is written down here at unusual length. The
renewal path is short. The things around it are not.

The client holds an access token with a short lifetime. When it expires, the
client exchanges its refresh token for a new access token. Nothing else changes:
the user sees nothing, the resource server sees a new signature, the session
identifier stays the same. Any implementation that mints a new session
identifier during renewal is doing more work than the protocol asks for, and the
extra work is observable in the audit log.

Renewal is scheduled, not opportunistic. A client that waits for a rejected call
before renewing has already paid the latency of the rejection, and a client that
renews on every call turns one token exchange per hour into one per request. The
window matters: renew when the access token has less than a third of its
lifetime left, so a slow network round trip does not push the exchange past the
moment the old token would have been rejected.

Concurrency is the part that is easy to get wrong. Six tabs waking at the same
minute each decide to renew, six exchanges race, the authorization server
invalidates the refresh token after the first, and five tabs receive an error
they cannot interpret. The fix is not a lock; the fix is that a rotation is
idempotent for a short grace period, so the second through sixth exchange
returns the same result as the first instead of a rejection.

The refresh token itself is stored where the browser will not read it in a page
context. A refresh token in local storage is a refresh token available to any
script that ever runs on the origin. This is the single most common
misconfiguration found in reviews, and it is not detectable from the token
itself.

Expiry of the refresh token is a different event from expiry of the access
token, and conflating the two produces bugs that only appear weeks later. The
access token expiring is routine. The refresh token expiring ends the session,
and the user has to authenticate again with credentials, which is a user-visible
event that belongs in product analytics rather than in a log line.

The audit log records four things: the session identifier, the old token's
expiry, the exchange timestamp, and whether the exchange was the first for that
session. That last field is the one that answers "did this user hit the race
condition", and it is worth carrying even though nothing reads it during normal
operation.

A revocation list is the escape hatch for a leaked refresh token. It is coarse,
it is slow to converge, and it should be reserved for the case where rotation
cannot be trusted. A user reporting a stolen device is served better by
deleting the session outright than by publishing a revocation list.

Finally, the operational note that appears in every incident review: renewal
failures are not renewal bugs. They are usually an authorization server that
lost its signing key, or a client whose clock drifted past the expiry. Log the
clock skew alongside the failure, because a ninety second skew looks exactly like
an expiry bug and is fixed in a completely different place.

The renewal path is correct when a user never notices it happening, when a
concurrent burst of tabs produces one exchange rather than six, and when the
session survives its own access token but not its own refresh token. Anything
else is a bug in the surrounding code, not in renewal.
