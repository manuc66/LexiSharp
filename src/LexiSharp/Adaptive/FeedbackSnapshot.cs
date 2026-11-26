namespace LexiSharp.Adaptive;

/// <summary>One category's masses in a persisted feedback memory.</summary>
/// <param name="Category">The category label.</param>
/// <param name="AcceptanceMass">The acceptance mass at the snapshot instant.</param>
/// <param name="RefusalMass">The refusal mass at the snapshot instant.</param>
public sealed record CategoryFeedbackSnapshot(
    string Category,
    double AcceptanceMass,
    double RefusalMass);

/// <summary>The global acceptance prior in a persisted feedback memory.</summary>
/// <param name="LastUpdated">The instant of the last global update.</param>
/// <param name="Categories">Acceptance masses by category, sorted ordinal.</param>
public sealed record GlobalFeedbackSnapshot(
    DateTimeOffset LastUpdated,
    IReadOnlyList<CategoryFeedbackSnapshot> Categories);

/// <summary>One context in a persisted feedback memory.</summary>
/// <param name="Key">The codec-encoded context key.</param>
/// <param name="LastUpdated">The instant of the context's last interaction.</param>
/// <param name="Exposures">Offers the user saw, including ignored ones.</param>
/// <param name="Responses">Offers the user answered: accepted, corrected or rejected.</param>
/// <param name="Observations">Every interaction in the context.</param>
/// <param name="Categories">Category masses, sorted ordinal.</param>
public sealed record ContextFeedbackSnapshot(
    string Key,
    DateTimeOffset LastUpdated,
    long Exposures,
    long Responses,
    long Observations,
    IReadOnlyList<CategoryFeedbackSnapshot> Categories);

/// <summary>A versioned, projected view of a <see cref="FeedbackMemory{TContext}"/>.</summary>
/// <remarks>
/// <para>
/// This is an in-memory data transfer object: the masses are already projected to
/// <see cref="SavedAt"/>, so loading it does not need the original clock. Persisting it is the
/// caller's choice, or the combined adaptive snapshot's job; this type carries no signature of
/// its own.
/// </para>
/// <para>
/// <see cref="ContextsHashed"/> is the codec's claim, not a guarantee: a custom codec that returns
/// clear keys must set it to <c>false</c>. A snapshot with clear keys is as sensitive as the log it
/// came from.
/// </para>
/// </remarks>
/// <param name="SchemaVersion">The format version; only <see cref="CurrentVersion"/> is read.</param>
/// <param name="CodecId">The codec identity; a loader with another codec refuses the snapshot.</param>
/// <param name="Policy">The recency policy the masses were projected with.</param>
/// <param name="Options">The bounds and smoothing the memory used.</param>
/// <param name="ContextsHashed">Whether the keys are opaque.</param>
/// <param name="SavedAt">The instant the masses were projected to.</param>
/// <param name="Global">The global acceptance prior.</param>
/// <param name="Contexts">The context entries, sorted by key ordinal.</param>
public sealed record FeedbackSnapshot(
    int SchemaVersion,
    string CodecId,
    RecencyPolicy Policy,
    FeedbackMemoryOptions Options,
    bool ContextsHashed,
    DateTimeOffset SavedAt,
    GlobalFeedbackSnapshot Global,
    IReadOnlyList<ContextFeedbackSnapshot> Contexts)
{
    /// <summary>The only feedback-snapshot version this build reads.</summary>
    public const int CurrentVersion = 1;
}
