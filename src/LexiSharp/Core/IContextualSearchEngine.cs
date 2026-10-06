namespace LexiSharp.Core;

/// <summary>
/// An <see cref="ITextSearchEngine"/> that accepts a caller-supplied payload alongside a query,
/// for the cases a query alone does not carry: who is asking, what time it is, which experiment
/// arm is running.
/// </summary>
/// <remarks>
/// <para>
/// This is an <b>optional capability</b>, in the same family as
/// <see cref="IFacetedSearchEngine"/> or <see cref="IDetailedSearchEngine"/>: a pipeline that
/// holds an <see cref="ITextSearchEngine"/> cannot tell from the type whether the payload will be
/// honoured, so it tests for it —
///
/// <code>
/// var results = engine is IContextualSearchEngine&lt;UserContext&gt; contextual
///     ? contextual.Search(query, options, user)
///     : engine.Search(query, options);
/// </code>
///
/// — and the branch that falls through is the caller's decision, not a silent default.
/// </para>
/// <para>
/// The payload type is the type parameter, not <c>object</c>. That is what keeps a contextual
/// engine from being handed the wrong state: <c>BoostedTextSearchEngine&lt;UserContext&gt;</c>
/// answers to <c>IContextualSearchEngine&lt;UserContext&gt;</c> and to nothing else, so a mismatch
/// does not compile. A pipeline that genuinely cannot name the type is the case that would need a
/// non-generic <c>object</c>-taking façade over this, and that façade does not exist — one contract
/// is worth more here than two that have to be kept in step.
/// </para>
/// <para>
/// Nothing about <see cref="SearchOptions"/> carries the payload, and that is deliberate: it is a
/// record with value equality and a serializable shape, and a caller-defined value in one of its
/// properties would put both at the mercy of whatever <c>Equals</c> the caller's type happens to
/// implement.
/// </para>
/// </remarks>
/// <typeparam name="TPayload">
/// The caller's per-search state. What it contains is the caller's business; this library passes it
/// through without reading it.
/// </typeparam>
public interface IContextualSearchEngine<TPayload> : ITextSearchEngine
{
    /// <summary>
    /// Searches as <see cref="ITextSearchEngine.Search(string, SearchOptions?)"/> does, with
    /// <paramref name="payload"/> handed to whatever in this pipeline asked for it.
    /// </summary>
    /// <param name="query">Raw query text; it is tokenized internally.</param>
    /// <param name="options">Optional search options (<see cref="SearchOptions.Default"/> when null).</param>
    /// <param name="payload">The caller's per-search state, forwarded to the components that accept it.</param>
    IReadOnlyList<SearchResult> Search(string query, SearchOptions? options, TPayload payload);
}
