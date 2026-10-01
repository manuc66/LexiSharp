namespace LexiSharp.Core;

/// <summary>
/// The answer a <see cref="IKnowledgeGraphBridge"/> returns for a sub-graph query around a set
/// of entities: the local facts that touch them, plus the community summaries the graph can
/// stand above them.
/// </summary>
/// <param name="MatchedTriplets">
/// The triplets in the queried neighbourhood: those whose subject or object is one of the seed
/// entities, within the requested hop budget. Empty when the neighbourhood is empty.
/// </param>
/// <param name="CommunitySummaries">
/// Summaries of the communities (clustered regions) the graph associates with the entities —
/// the "so what" beyond the raw facts. Empty when the graph provides none.
/// </param>
public sealed record GraphContext(
    IReadOnlyList<EntityTriplet> MatchedTriplets,
    IReadOnlyList<string> CommunitySummaries)
{
    /// <summary>An empty context: no facts and no summaries.</summary>
    public static readonly GraphContext Empty =
        new(Array.Empty<EntityTriplet>(), Array.Empty<string>());
}