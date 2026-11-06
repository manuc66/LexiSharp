namespace LexiSharp.Core;

/// <summary>
/// Scores whether a candidate passage is <b>necessary evidence</b> for answering a question,
/// conditioned on the evidence already retrieved — the seam for a contextual decision model
/// (JEV and its open, locally-runnable alternatives such as jevos, Von, Decider, ...).
/// <b>LexiSharp never runs such a model itself</b>: implementing this interface is up to the
/// consumer — a local ONNX/OpenVINO model, an HTTP call to a model server, ... — and the
/// multi-hop expansion engine built on top only reads the returned score.
/// </summary>
/// <remarks>
/// <para>
/// This is the contextual counterpart of <see cref="ICrossEncoderScorer"/>. Where a cross-encoder
/// judges a (query, document) pair in isolation, a contextual evidence scorer judges a candidate
/// <i>in the light of the evidence chain already assembled</i>: a passage that is only a
/// "bridge" between two facts can become necessary once the intermediate fact has been retrieved,
/// even though its direct match to the original question is weak. The score is a probability in
/// <c>[0, 1]</c> (1 = necessary evidence, 0 = not a match / optional background), and consuming
/// engines reject NaN and infinite values, as everywhere in LexiSharp.
/// </para>
/// <para>
/// The context is the list of passages already retrieved and retained (with their tentative
/// scores), never gold labels or answers. Implementations must treat the supplied text as quoted
/// data and use no outside knowledge, matching the decision instruction of the reference
/// architecture this seam targets.
/// </para>
/// </remarks>
public interface IContextualEvidenceScorer
{
    /// <summary>Human readable name of the underlying model, e.g. <c>"jevos-openvino-int8"</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Judges how necessary <paramref name="candidate"/> is as evidence for
    /// <paramref name="query"/>, given the already retrieved <paramref name="context"/>.
    /// Higher values mean more necessary; <c>0</c> conventionally means "not a match".
    /// </summary>
    /// <param name="query">The original question.</param>
    /// <param name="candidate">The candidate passage to judge.</param>
    /// <param name="context">The passages (and tentative scores) already retained; empty for the initial round.</param>
    double Score(string query, SearchDocument candidate, IReadOnlyList<SearchResult> context);
}

/// <summary>
/// Asynchronous counterpart of <see cref="IContextualEvidenceScorer"/>: scores a
/// (query, candidate, context) triple without blocking the caller. Use this when the model is
/// remote or otherwise async.
/// </summary>
public interface IAsyncContextualEvidenceScorer
{
    /// <summary>Human readable name of the underlying model.</summary>
    string Name { get; }

    /// <summary>Judges how necessary <paramref name="candidate"/> is as evidence for <paramref name="query"/>, given <paramref name="context"/>.</summary>
    Task<double> ScoreAsync(
        string query,
        SearchDocument candidate,
        IReadOnlyList<SearchResult> context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Adapts a synchronous <see cref="IContextualEvidenceScorer"/> to
/// <see cref="IAsyncContextualEvidenceScorer"/> by running it on the calling thread inside a
/// completed task. Useful for reusing an in-process model (OpenVINO/ONNX) behind an async consumer.
/// </summary>
public sealed class SyncContextualEvidenceScorerAdapter : IAsyncContextualEvidenceScorer
{
    private readonly IContextualEvidenceScorer _inner;

    /// <param name="inner">The synchronous scorer to adapt.</param>
    public SyncContextualEvidenceScorerAdapter(IContextualEvidenceScorer inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <inheritdoc />
    public Task<double> ScoreAsync(
        string query,
        SearchDocument candidate,
        IReadOnlyList<SearchResult> context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_inner.Score(query, candidate, context));
}