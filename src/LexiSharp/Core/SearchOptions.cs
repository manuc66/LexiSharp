namespace LexiSharp.Core;

/// <summary>
/// Controls how a search is performed and how results are returned.
/// </summary>
/// <param name="Limit">Maximum number of results to return.</param>
/// <param name="MinimumScore">Results with a score below this value are discarded.</param>
public sealed record SearchOptions(
    int Limit = 10,
    double MinimumScore = double.NegativeInfinity)
{
    public static readonly SearchOptions Default = new();
}