namespace LexiSharp.Linguistics;

/// <summary>
/// Raw synonym edges: one-way rewrites (<see cref="Add"/>) and bidirectional equivalence
/// groups (<see cref="AddEquivalent"/>). Entries are plain text; the search engine tokenizes
/// them with its own tokenizer at construction and rejects anything that does not reduce to
/// exactly one term.
/// </summary>
/// <remarks>
/// Expansion is one level deep and non-transitive: a query term pulls in its direct synonyms
/// only — never synonyms of synonyms. Not thread-safe while being built; treat the instance
/// as immutable once handed to an engine.
/// </remarks>
public sealed class SynonymMap
{
    private readonly List<(string Source, string Target)> _oneWay = new();
    private readonly List<string[]> _groups = new();

    /// <summary>Whether no edge has been registered.</summary>
    public bool IsEmpty => _oneWay.Count == 0 && _groups.Count == 0;

    /// <summary>The one-way edges, in registration order.</summary>
    internal IReadOnlyList<(string Source, string Target)> OneWay => _oneWay;

    /// <summary>The equivalence groups, in registration order.</summary>
    internal IReadOnlyList<string[]> Groups => _groups;

    /// <summary>
    /// Registers a one-way edge: querying <paramref name="term"/> also searches
    /// <paramref name="synonym"/> — but not the reverse. Returns this instance (fluent).
    /// </summary>
    public SynonymMap Add(string term, string synonym)
    {
        ArgumentNullException.ThrowIfNull(term);
        ArgumentNullException.ThrowIfNull(synonym);

        _oneWay.Add((term, synonym));
        return this;
    }

    /// <summary>
    /// Registers a bidirectional equivalence group: every member is a synonym of every other
    /// member, in both directions. Returns this instance (fluent).
    /// </summary>
    public SynonymMap AddEquivalent(params string[] terms)
    {
        ArgumentNullException.ThrowIfNull(terms);

        if (terms.Length < 2)
            throw new ArgumentException("An equivalence group needs at least two terms.", nameof(terms));

        foreach (var term in terms)
            ArgumentNullException.ThrowIfNull(term);

        _groups.Add((string[])terms.Clone());
        return this;
    }
}
