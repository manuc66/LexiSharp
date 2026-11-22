# Migration guide — 1.0.0 → 1.1.0

## Segment format (v5 → v6)

1.1.0 keeps writing **v6** segments. A **v5** segment is now read in *degraded mode* instead of
being refused:

- term postings, documents and lengths are read exactly as before;
- `TryGetTermFrequencyBound` returns `false`, so `IScoreBoundScorer` declines and
  `UpperBoundScoreNormalizer` falls back to `MaxScoreNormalizer` until the index is rebuilt;
- a `Trace.TraceWarning` is emitted when such a segment is opened.

**Recommended action:** rebuild the index (or run `lexisharp migrate-segment`) to restore score
bounds. Segments older than v5 are still refused — rebuild them.

## PostgreSQL `TieBreak.InsertionOrder`

SQL backends cannot honour `TieBreak.InsertionOrder` (there is no ordinal position in a table).
They throw `NotSupportedException` before opening a connection. Use `TieBreak.DocumentId` (the
default), implemented as `id COLLATE "C"`.

## TF-IDF repeated query terms

TF-IDF no longer multiplies a document's score by the number of occurrences of a repeated query
term on the accumulating path. Rankings that depended on the old behaviour may move; the change is
a correctness fix, not a tuning change.
