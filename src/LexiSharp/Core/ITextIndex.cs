using System.Collections.Generic;

namespace LexiSharp.Core;

/// <summary>
/// A write-capable index: the read surface of <see cref="IReadOnlyTextIndex"/> plus the mutations
/// a caller that owns the corpus performs (<c>Index</c>/<c>Add</c>/<c>Remove</c>/<c>Clear</c>).
/// </summary>
/// <remarks>
/// <para>
/// This type is what an engine holds when it needs to forward writes; the scoring seams
/// (<see cref="ITextScorer.Score"/>, <see cref="IScoreExplainer.Explain"/>, a query plan) receive
/// only <see cref="IReadOnlyTextIndex"/>, so a scorer is never handed write access. Capabilities
/// beyond the flat surface are separate interfaces on the read view
/// (<see cref="ICandidateIndex"/>, <see cref="IVocabularyIndex"/>,
/// <see cref="IFieldStatisticsIndex"/>), detected with pattern matching.
/// </para>
/// </remarks>
public interface ITextIndex : IReadOnlyTextIndex
{
    /// <summary>Indexes a batch of documents, replacing any previously indexed content.</summary>
    void Index(IEnumerable<SearchDocument> documents);

    /// <summary>Adds a single document to the index. Replacing an existing id is allowed.</summary>
    void Add(SearchDocument document);

    /// <summary>Removes the document with the given id, if present.</summary>
    bool Remove(string documentId);

    /// <summary>Drops every document and statistic from the index.</summary>
    void Clear();
}