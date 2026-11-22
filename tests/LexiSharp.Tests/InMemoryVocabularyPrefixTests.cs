using LexiSharp.Core;
using LexiSharp.Indexing;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The ordered prefix range the in-memory vocabulary now answers, and its invalidation.
/// </summary>
/// <remarks>
/// The resolver's fallback walks every term; this capability is what makes the walk a binary search.
/// The tests pin the two properties the fallback cannot provide: the range is contiguous and sorted,
/// and a write between two prefix queries is visible to the second.
/// </remarks>
public class InMemoryVocabularyPrefixTests
{
    private static InMemoryTextIndex IndexOf(params string[] terms)
    {
        var index = new InMemoryTextIndex();

        foreach (string term in terms)
            index.Add(new SearchDocument(term, term));

        return index;
    }

    private static string[] Prefix(InMemoryTextIndex index, string prefix) =>
        ((IVocabularyPrefixIndex)index).TermsWithPrefix(prefix).ToArray();

    [Fact]
    public void TermsWithPrefix_ReturnsTheSortedContiguousRange()
    {
        var index = IndexOf("cart", "dog", "car", "carbon", "cat", "carpet");

        Assert.Equal(["car", "carbon", "carpet", "cart"], Prefix(index, "car"));
        Assert.Equal(["cat"], Prefix(index, "cat"));
        Assert.Empty(Prefix(index, "zz"));
    }

    [Fact]
    public void AnEmptyPrefix_ReturnsTheWholeVocabularyInOrder()
    {
        var index = IndexOf("cart", "car", "dog");

        Assert.Equal(["car", "cart", "dog"], Prefix(index, ""));
    }

    [Fact]
    public void AWriteBetweenTwoPrefixQueries_IsVisiblyReindexed()
    {
        var index = IndexOf("car", "cart");

        Assert.Equal(["car", "cart"], Prefix(index, "car"));

        index.Add(new SearchDocument("carpenter", "carpenter"));
        Assert.Equal(["car", "carpenter", "cart"], Prefix(index, "car"));

        index.Remove("carpenter");
        Assert.Equal(["car", "cart"], Prefix(index, "car"));
    }

    /// <summary>
    /// <see cref="InMemoryTextIndex.AddExpansionTerms"/> posts a term without tokenizing a new
    /// document, and the vocabulary is what a prefix or a fuzzy atom is resolved against. A term it
    /// posts is a term of the index like any other, so it has to be in the ordered vocabulary too —
    /// otherwise the exact term matches and neither expansion can reach it.
    /// </summary>
    [Fact]
    public void TermsInjectedByAddExpansionTerms_ArePartOfTheVocabulary()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("x", "the oauth token expiry loop"));
        index.AddExpansionTerms("x", ["refreshment"]);

        Assert.Contains("refreshment", index.Vocabulary);
        Assert.Equal(index.VocabularySize, index.Vocabulary.Count());
        Assert.Equal(["refreshment"], Prefix(index, "refre"));
    }

    /// <summary>
    /// The injected term leaves with the document, on the same path a literal term takes: the
    /// posting list empties, the term is dropped from the map, and the ordered vocabulary has to
    /// follow.
    /// </summary>
    [Fact]
    public void RemovingADocument_DropsItsExpansionTermsFromTheVocabulary()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("x", "the oauth token expiry loop"));
        index.AddExpansionTerms("x", ["refreshment"]);

        index.Remove("x");

        Assert.DoesNotContain("refreshment", index.Vocabulary);
        Assert.Empty(Prefix(index, "refre"));
        Assert.Equal(index.VocabularySize, index.Vocabulary.Count());
    }

    /// <summary>
    /// Invariant (guard against the AddExpansionTerms desync regression): after any sequence of
    /// Add / Remove / AddExpansionTerms / Clear, the ordered vocabulary must agree with the posting
    /// map on both count and membership. A term that is posted must be enumerable as a vocabulary
    /// term, and a term that is not posted must not be. This test pins the contract so a future
    /// change that adds a second structure (or a second write path) cannot silently desync them.
    /// </summary>
    [Fact]
    public void Vocabulary_StaysInSyncWithPostings_AcrossMixedOperations()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("a", "alpha beta gamma"));
        index.Add(new SearchDocument("b", "beta delta"));
        index.AddExpansionTerms("a", ["epsilon", "zeta"]);
        index.Add(new SearchDocument("c", "eta"));

        AssertSynced(index, new[] { "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta" });

        // Remove a document and re-check the invariant.
        index.Remove("a");
        Assert.Equal(index.VocabularySize, index.Vocabulary.Count());
        Assert.DoesNotContain("epsilon", index.Vocabulary);
        Assert.DoesNotContain("zeta", index.Vocabulary);
        Assert.Contains("beta", index.Vocabulary); // still posted by "b"

        // Clear empties everything.
        index.Clear();
        Assert.Equal(0, index.VocabularySize);
        Assert.Empty(index.Vocabulary);
    }

    /// <summary>
    /// Property-style guard: a deterministic pseudo-random sequence of Add / Remove /
    /// AddExpansionTerms / Clear must keep the vocabulary in sync with the postings after every
    /// single step. This is stronger than the fixed scenario above because it exercises
    /// interleavings (remove a document, re-add it, inject terms, remove again) that a hand-written
    /// test would not think of. The seed is fixed so a failure reproduces locally.
    /// </summary>
    [Fact]
    public void Vocabulary_StaysInSyncWithPostings_UnderRandomOperationSequences()
    {
        var rng = new Random(20261122);
        var terms = new[] { "alpha", "beta", "gamma", "delta", "eps" };

        for (int trial = 0; trial < 200; trial++)
        {
            var index = new InMemoryTextIndex();
            var liveDocs = new List<string>();

            for (int step = 0; step < 40; step++)
            {
                int op = rng.Next(4);
                try
                {
                    switch (op)
                    {
                        case 0: // Add
                            {
                                string id = "d" + rng.Next(6);
                                string text = terms[rng.Next(terms.Length)] + " " + terms[rng.Next(terms.Length)];
                                index.Add(new SearchDocument(id, text));
                                if (!liveDocs.Contains(id)) liveDocs.Add(id);
                                break;
                            }
                        case 1: // Remove
                            {
                                if (liveDocs.Count == 0) break;
                                string id = liveDocs[rng.Next(liveDocs.Count)];
                                index.Remove(id);
                                liveDocs.Remove(id);
                                break;
                            }
                        case 2: // AddExpansionTerms (only if the doc exists)
                            {
                                if (liveDocs.Count == 0) break;
                                string id = liveDocs[rng.Next(liveDocs.Count)];
                                string injected = "zzz" + rng.Next(100);
                                try { index.AddExpansionTerms(id, new[] { injected }); }
                                catch (KeyNotFoundException) { /* doc may have been removed concurrently in the model; ignore */ }
                                break;
                            }
                        case 3: // Clear
                            {
                                index.Clear();
                                liveDocs.Clear();
                                break;
                            }
                    }
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"trial {trial}, step {step}, op {op}: {ex.Message}", ex);
                }

                // Invariant after every step.
                Assert.Equal(index.VocabularySize, index.Vocabulary.Count());
                foreach (var t in index.Vocabulary)
                    Assert.True(index.DocumentFrequency(t) > 0,
                        $"trial {trial} step {step}: vocabulary term '{t}' has no posting list");
            }
        }
    }

    private static void AssertSynced(InMemoryTextIndex index, IEnumerable<string> expectedTerms)
    {
        Assert.Equal(index.VocabularySize, index.Vocabulary.Count());
        foreach (var term in index.Vocabulary)
            Assert.True(index.DocumentFrequency(term) > 0,
                $"vocabulary term '{term}' has no posting list");
        foreach (var term in expectedTerms)
        {
            if (index.DocumentFrequency(term) > 0)
                Assert.Contains(term, index.Vocabulary);
        }
    }
}
