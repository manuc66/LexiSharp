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
    /// The order the engine produced the candidates in, which for a text index is the order documents
    /// were added.
    /// </summary>
    /// <remarks>
    /// Use this to reproduce a system that breaks ties by insertion order: its figures depend on the
    /// order its corpus was loaded, so a different load order gives a different ranking among equal
    /// scores, and only this option can reproduce it.
    /// <para>
    /// The order is still total and deterministic — it depends on the index's contents, not on the
    /// filesystem — but it is no longer independent of how the corpus was assembled. Adding one document
    /// can reorder two unrelated ones that happen to tie, so results are reproducible for a given index
    /// rather than for a given set of documents.
    /// </para>
    /// </remarks>
    InsertionOrder = 1,
}