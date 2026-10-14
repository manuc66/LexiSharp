namespace LexiSharp.Ranking;

/// <summary>
/// How the engines order candidates whose scores are exactly equal.
/// </summary>
/// <remarks>
/// Two documents with the same score have no ranking between them, so something has to decide, and
/// each choice is a convention rather than a fact. Neither is better; they answer different questions,
/// and a figure published by one implementation is only comparable with a reader that knows which
/// convention produced it.
/// </remarks>
public enum TieBreak
{
    /// <summary>
    /// Lower document id first, ordinally. The default.
    /// </summary>
    /// <remarks>
    /// Deterministic across machines and across runs, which is why it is the default: tie-breaking on
    /// the order candidates were enumerated in made the same checkout rank differently on a developer
    /// machine and on a CI runner, since that order came from the filesystem. When two documents really
    /// do score identically, this orders them by name — stable, but arbitrary.
    /// </remarks>
    DocumentId = 0,

    /// <summary>
    /// The order documents were added, as the index holds it: the document's ordinal, which is the
    /// position it occupies in the index's ordinal space.
    /// </summary>
    /// <remarks>
    /// Use this to reproduce a system that breaks ties by insertion order: its figures depend on the
    /// order its corpus was loaded, so a different load order gives a different ranking among equal
    /// scores, and only this option can reproduce it.
    /// <para>
    /// The order is total and deterministic, and it is the same whichever path served the query: the
    /// term-at-a-time pass and the per-document loop produce candidates in different orders, and the
    /// document's ordinal is the one thing both carry. It does not move with the corpus size either,
    /// which decided the path, and so decided the tied order, before.
    /// </para>
    /// <para>
    /// Two consequences of reading the ordinal rather than a counter. A document removed frees its
    /// ordinal, and the next document added takes that ordinal, so a re-added document takes the
    /// position of the slot it was handed rather than the end of the corpus. And an index that cannot
    /// enumerate its ordinals — a backend, or a caller's own — has no ordinal to read, so the order
    /// follows the order it enumerates its documents in, which is the only insertion order such an
    /// index can state.
    /// </para>
    /// <para>
    /// The order is still not independent of how the corpus was assembled: results are reproducible
    /// for a given index rather than for a given set of documents, because adding a document moves the
    /// statistics, and with them which documents come out level.
    /// </para>
    /// </remarks>
    InsertionOrder = 1,
}