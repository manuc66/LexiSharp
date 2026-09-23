namespace LexiSharp.AspNetCore;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Query-string parameters of a <see cref="LexiSharpSearchEndpoint"/> request, bound by
/// ASP.NET Core model binding. Mirror the knobs of <see cref="LexiSharpQueryOptions"/>.
/// </summary>
public sealed record LexiSharpSearchParameters
{
    /// <summary>The raw search query. Maps to the <c>q</c> query-string parameter.</summary>
    [FromQuery(Name = "q")]
    public string Query { get; init; } = string.Empty;

    /// <summary>Maximum number of hits to return. Defaults to 10.</summary>
    public int Limit { get; init; } = 10;

    /// <summary>Number of top-ranked hits to skip, for deep pagination. Defaults to 0.</summary>
    public int Offset { get; init; }

    /// <summary>Discard hits with a score below this value. Defaults to <c>-∞</c>.</summary>
    public double MinimumScore { get; init; } = double.NegativeInfinity;

    /// <summary>Wrap matched terms in the hit text. Defaults to <c>false</c>.</summary>
    public bool Highlight { get; init; }
}