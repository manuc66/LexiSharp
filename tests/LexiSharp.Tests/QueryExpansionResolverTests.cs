using System.Diagnostics.CodeAnalysis;
using LexiSharp.Core;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The two properties a prefix/fuzzy atom's resolution has to hold: the vocabulary is walked once
/// per <i>distinct</i> atom rather than once per occurrence, and a query cannot ask for an unbounded
/// number of walks. Both are cost properties, so they are pinned against an index that counts its own
/// enumeration instead of against a stopwatch.
/// </summary>
public class QueryExpansionResolverTests
{
    [Fact]
    public void Resolve_RepeatedAtom_WalksTheVocabularyOnce()
    {
        var index = new CountingVocabularyIndex("catt", "cat", "cats");
        var resolver = new QueryExpansionResolver(index, fuzzyOnlyOutOfVocabulary: false);
        var atom = new QueryExpansion("catt", QueryExpansionKind.Fuzzy);

        string[] first = resolver.Resolve(atom);

        for (int i = 0; i < 20; i++)
            Assert.Equal(first, resolver.Resolve(atom));

        // Closest first: the exact term, then the two one-edit neighbours in ordinal order.
        Assert.Equal(new[] { "catt", "cat", "cats" }, first);
        Assert.Equal(1, index.VocabularyEnumerations);
    }

    [Fact]
    public void Resolve_AtomsDifferingOnlyByOperator_ShareOneWalk()
    {
        var index = new CountingVocabularyIndex("catt", "cat");
        var resolver = new QueryExpansionResolver(index, fuzzyOnlyOutOfVocabulary: false);

        resolver.Resolve(new QueryExpansion("catt", QueryExpansionKind.Fuzzy));
        resolver.Resolve(new QueryExpansion("catt", QueryExpansionKind.Fuzzy) { Operator = QueryTermOperator.Required });
        resolver.Resolve(new QueryExpansion("catt", QueryExpansionKind.Fuzzy) { Operator = QueryTermOperator.Prohibited });

        // The operator decides what the atom gates on, not what it resolves to: one walk answers all
        // three, and the gate still receives the terms it asks for.
        Assert.Equal(1, index.VocabularyEnumerations);
    }

    [Fact]
    public void Resolve_DistinctAtoms_WalkOnceEach()
    {
        var index = new CountingVocabularyIndex("catt", "cat");
        var resolver = new QueryExpansionResolver(index, fuzzyOnlyOutOfVocabulary: false);

        resolver.Resolve(new QueryExpansion("catt", QueryExpansionKind.Fuzzy));
        resolver.Resolve(new QueryExpansion("catt", QueryExpansionKind.Fuzzy, MaxEdits: 2));
        resolver.Resolve(new QueryExpansion("cat", QueryExpansionKind.Prefix));

        Assert.Equal(3, index.VocabularyEnumerations);
    }

    [Fact]
    public void Resolve_AtTheCap_IsHonored_AndOnePastIt_IsRefused()
    {
        var index = new CountingVocabularyIndex("term");
        var resolver = new QueryExpansionResolver(index, fuzzyOnlyOutOfVocabulary: false);

        for (int i = 0; i < RankedTextSearchEngine.MaxExpansionAtoms; i++)
            resolver.Resolve(new QueryExpansion($"atom{i}", QueryExpansionKind.Fuzzy));

        var exception = Assert.Throws<ArgumentException>(
            () => resolver.Resolve(new QueryExpansion("one-too-many", QueryExpansionKind.Fuzzy)));

        Assert.Contains(RankedTextSearchEngine.MaxExpansionAtoms.ToString(), exception.Message);
        Assert.Equal(RankedTextSearchEngine.MaxExpansionAtoms, index.VocabularyEnumerations);
    }

    [Fact]
    public void Resolve_IndexWithoutAVocabulary_ReturnsTheLiteralBase()
    {
        // The fallback a plain query takes on an index that cannot enumerate: no walk, no atom
        // budget spent, and a `foo~` behaves as `foo`.
        var resolver = new QueryExpansionResolver(null, fuzzyOnlyOutOfVocabulary: false);

        Assert.Equal(new[] { "catt" }, resolver.Resolve(new QueryExpansion("catt", QueryExpansionKind.Fuzzy)));
    }

    [Fact]
    public void Resolve_FuzzyOnlyOutOfVocabulary_KnownBaseIsNotExpanded()
    {
        var index = new CountingVocabularyIndex("catt", "cat")
        {
            DocumentFrequencyOf = term => term is "catt" or "cat" ? 3 : 0,
        };
        var resolver = new QueryExpansionResolver(index, fuzzyOnlyOutOfVocabulary: true);

        // Both are in the vocabulary, so neither pays a walk: the exact term is the answer.
        Assert.Equal(new[] { "catt" }, resolver.Resolve(new QueryExpansion("catt", QueryExpansionKind.Fuzzy)));
        Assert.Equal(new[] { "cat" }, resolver.Resolve(new QueryExpansion("cat", QueryExpansionKind.Fuzzy)));
        Assert.Equal(0, index.VocabularyEnumerations);

        // An unknown base is the case the mode exists for, and it is the one that walks.
        Assert.Empty(resolver.Resolve(new QueryExpansion("zzz", QueryExpansionKind.Fuzzy)));
        Assert.Equal(1, index.VocabularyEnumerations);
    }

    /// <summary>
    /// A vocabulary index that counts how many times its terms were enumerated and answers the
    /// statistics questions with a fixed document frequency.
    /// </summary>
    private sealed class CountingVocabularyIndex(params string[] terms) : IVocabularyIndex
    {
        private readonly string[] _terms = terms;

        public int VocabularyEnumerations { get; private set; }

        public Func<string, int> DocumentFrequencyOf { get; init; } = _ => 0;

        public IEnumerable<string> Vocabulary
        {
            get
            {
                VocabularyEnumerations++;
                return _terms;
            }
        }

        public IReadOnlyCollection<SearchDocument> Documents => Array.Empty<SearchDocument>();

        public int Count => 0;

        public double AverageDocumentLength => 0;

        public int VocabularySize => _terms.Length;

        public long CorpusTokenCount => 0;

        public bool Contains(string documentId) => false;

        public IReadOnlyList<string> GetTerms(string documentId) => Array.Empty<string>();

        public IReadOnlyList<int> GetTermPositions(string documentId, string term) => Array.Empty<int>();

        public int DocumentFrequency(string term) => DocumentFrequencyOf(term);

        public int CorpusFrequency(string term) => 0;

        public int TermFrequency(string documentId, string term) => 0;

        public int DocumentLength(string documentId) => 0;

        public bool TryGetDocument(string documentId, [NotNullWhen(true)] out SearchDocument? document)
        {
            document = null;
            return false;
        }
    }
}
