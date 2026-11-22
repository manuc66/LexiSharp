namespace LexiSharp.Core;

/// <summary>One vocabulary term within an edit budget, and the distance that admitted it.</summary>
/// <param name="Term">The indexed term.</param>
/// <param name="Distance">Levenshtein distance from the query's base term.</param>
internal readonly record struct FuzzyVocabularyMatch(string Term, int Distance);

/// <summary>
/// Capability for an index that can find the vocabulary terms within an edit distance without
/// comparing the base against every term.
/// </summary>
/// <remarks>
/// The fallback is <see cref="IVocabularyIndex.Vocabulary"/> plus a distance per term. A trie with a
/// Levenshtein row prunes a subtree when every cell of the row is already above the budget, because
/// extending both strings cannot lower the prefix distance. The returned distance is the one the
/// structure computed, so a caller that orders by distance does not compute it again.
/// </remarks>
internal interface IVocabularyFuzzyIndex
{
    /// <summary>
    /// Tries to return every indexed term within <paramref name="maxEdits"/> edits of
    /// <paramref name="term"/>, with its distance.
    /// </summary>
    /// <remarks>
    /// <c>false</c> means this index cannot serve the query with its fuzzy structure — an empty
    /// vocabulary, a negative budget, or a vocabulary large enough that building the structure would
    /// cost more than the walk. The caller must then use the vocabulary walk; returning an empty
    /// list with <c>true</c> would turn a declined structure into “no matches”, which is a different
    /// answer.
    /// </remarks>
    /// <param name="term">The normalized base term, operator stripped.</param>
    /// <param name="maxEdits">Non-negative edit budget, clamped by the caller to the query syntax.</param>
    /// <param name="matches">The matches on success; empty otherwise.</param>
    bool TryGetTermsWithinEdits(
        string term,
        int maxEdits,
        out IReadOnlyList<FuzzyVocabularyMatch> matches);
}
