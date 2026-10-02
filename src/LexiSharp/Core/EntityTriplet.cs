namespace LexiSharp.Core;

/// <summary>
/// One fact of a knowledge graph: a subject and an object linked by a predicate — the
/// subject-verb-object shape relation extraction produces (« acme » → « supplies » →
/// « pumps »).
/// </summary>
public sealed record EntityTriplet
{
    /// <summary>Creates a triplet; every component must be non-blank.</summary>
    /// <param name="subject">The entity or concept the relation starts from.</param>
    /// <param name="predicate">The relation itself.</param>
    /// <param name="object">The entity or concept the relation points to.</param>
    /// <exception cref="ArgumentException">A blank component.</exception>
    public EntityTriplet(string subject, string predicate, string @object)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(predicate);
        ArgumentException.ThrowIfNullOrWhiteSpace(@object);

        Subject = subject;
        Predicate = predicate;
        Object = @object;
    }

    /// <summary>The entity or concept the relation starts from.</summary>
    public string Subject { get; }

    /// <summary>The relation itself.</summary>
    public string Predicate { get; }

    /// <summary>The entity or concept the relation points to.</summary>
    public string Object { get; }
}