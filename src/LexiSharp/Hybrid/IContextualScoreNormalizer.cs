using LexiSharp.Core;

namespace LexiSharp.Hybrid;

/// <summary>
/// A score normalizer that can use a source engine's query context, when the merger has one, instead
/// of measuring the result list alone.
/// </summary>
/// <remarks>
/// <para>
/// The inherited <see cref="IScoreNormalizer.Measure"/> remains the contract for a caller that has no
/// context: a normalizer implementing this interface must still return a usable mapping from the list,
/// because that is what a standalone merger or a third-party caller invokes.
/// </para>
/// <para>
/// The contextual overload is the one a hybrid uses when a source engine implemented
/// <see cref="IScoreBoundSearchEngine"/> and the merger implements <see cref="IContextualResultMerger"/>.
/// A normalizer that does not implement this interface ignores the context, which is why adding the
/// seam changes no existing pipeline's scores.
/// </para>
/// </remarks>
public interface IContextualScoreNormalizer : IScoreNormalizer
{
    /// <summary>
    /// Measures one engine's result list using what the engine reported about the query.
    /// </summary>
    /// <param name="results">One engine's ordered results for one query; empty when it matched nothing.</param>
    /// <param name="context">The engine's query context; never <c>null</c>.</param>
    /// <returns>The mapping, or <see cref="ScoreNormalization.None"/> when no scale is available.</returns>
    ScoreNormalization Measure(
        IReadOnlyList<SearchResult> results,
        ScoreNormalizationContext context);
}
