namespace LexiSharp.Core;

/// <summary>
/// Capability for a scorer whose score is exactly <c>0</c> for every document sharing no
/// query term with the query, which lets the search engine skip such documents entirely.
/// </summary>
/// <remarks>
/// <para>
/// Implemented by the built-in scorers (BM25, TF-IDF, query likelihood, boolean): they all
/// return <c>0</c> when the document contains none of the query terms, matching the engine's
/// « score 0 means no match » convention.
/// </para>
/// <para>
/// A custom scorer that can score <c>≠ 0</c> documents without shared terms (a
/// language-model or semantic scorer, for instance) must not implement this interface: the
/// engine then falls back to scoring every document, which remains correct.
/// </para>
/// </remarks>
public interface ITermOverlapScorer : ITextScorer;
