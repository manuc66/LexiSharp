namespace LexiSharp.Core;

/// <summary>
/// One fact of a knowledge graph: a subject and an object linked by a predicate — the
/// subject-verb-object shape relation extraction produces (« acme » → « supplies » →
/// « pumps »).
/// </summary>
/// <param name="Subject">The entity or concept the relation starts from.</param>
/// <param name="Predicate">The relation itself.</param>
/// <param name="Object">The entity or concept the relation points to.</param>
public sealed record EntityTriplet
{
    /// <summary>Creates a triplet; every component must be non-blank.</summary>
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