using System.Diagnostics.CodeAnalysis;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// A scorer's theoretical query bound, and the property that makes it usable: no document in the
/// corpus scores above it.
/// </summary>
/// <remarks>
/// The tests enumerate the corpus and score every document, because a bound is only worth having if
/// the document that would violate it has been looked for. A test that compares the bound with the
/// page maximum would pass on a corpus whose worst document never reached the page, which is the
/// case the bound exists for.
/// </remarks>
public class ScoreBoundTests
{
    private static InMemoryTextIndex Corpus()
    {
        var index = new InMemoryTextIndex();

        index.Index(
        [
            new SearchDocument("a", "alpha beta gamma"),
            new SearchDocument("b", "alpha alpha beta"),
            new SearchDocument("c", "alpha alpha alpha alpha alpha delta epsilon zeta eta theta"),
            new SearchDocument("d", "beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma tau"),
            new SearchDocument("e", "gamma"),
            new SearchDocument("f", "alpha beta gamma delta epsilon zeta eta theta iota kappa"),
        ]);

        return index;
    }

    private static IReadOnlyList<ITextScorer> Scorers() =>
    [
        new Bm25Scorer(),
        new Bm25Scorer(0.9, 0.4),
        new Bm25Scorer(13.0, 0.9),
        new Bm25Scorer(1.5, 0.75, arithmetic: Bm25Arithmetic.SinglePrecision),
        new Bm25Scorer(1.5, 0.75, saturationConstant: false),
        new Bm25Scorer(0.0, 0.75),
        new Bm25Scorer(1.5, 0.0),
        new Bm25Scorer(1.5, 1.0),
        new Bm25PlusScorer(),
        new Bm25PlusScorer(1.2, 0.6, delta: 0.5),
        new Bm25LScorer(),
        new Bm25LScorer(1.2, 0.6, delta: 0.25),
        new TfIdfScorer(),
    ];

    [Theory]
    [InlineData("alpha")]
    [InlineData("alpha beta")]
    [InlineData("gamma delta epsilon")]
    [InlineData("quokka")]
    [InlineData("alpha quokka")]
    public void EveryAdvertisedBound_IsAtLeastEveryDocumentScore(string query)
    {
        var index = Corpus();
        var terms = Tokenizer.Default.Tokenize(query);

        foreach (ITextScorer scorer in Scorers())
        {
            Assert.True(scorer is IScoreBoundScorer, scorer.Name);

            var bounded = (IScoreBoundScorer)scorer;
            Assert.True(bounded.TryGetQueryBound(terms, index, out QueryScoreBound bound), scorer.Name);

            double observed = 0;

            foreach (SearchDocument document in index.Documents)
            {
                double score = scorer.Score(document.Id, terms, index);

                if (score > observed)
                    observed = score;
            }

            Assert.True(
                bound.UpperBound + 1e-12 >= observed,
                $"{scorer.Name} bound {bound.UpperBound} is below the corpus maximum {observed} for '{query}'");

            Assert.Equal(scorer.Name, bound.ScorerName);
            Assert.Equal(terms.Distinct(StringComparer.Ordinal).Count(), bound.Terms.Count);
        }
    }

    [Fact]
    public void AnAbsentTerm_ContributesZeroAndDoesNotDeclineTheBound()
    {
        var index = Corpus();
        var scorer = (IScoreBoundScorer)new Bm25Scorer();
        var terms = Tokenizer.Default.Tokenize("alpha quokka");

        Assert.True(scorer.TryGetQueryBound(terms, index, out QueryScoreBound bound));

        TermScoreBound absent = bound.Terms.Single(term => term.Term == "quokka");

        Assert.Equal(0, absent.DocumentFrequency);
        Assert.Equal(0, absent.MaxTermFrequency);
        Assert.Equal(0, absent.MaxContribution);
        Assert.True(bound.UpperBound > 0);
    }

    [Fact]
    public void AQueryWeight_MultipliesTheTermBoundAndTheCorpusMaximum()
    {
        var index = Corpus();
        var terms = Tokenizer.Default.Tokenize("alpha");

        var prototype = new Bm25Scorer();
        var weighted = (IScoreBoundScorer)((IWeightedQueryScorer)prototype).WithTermWeights(
            new Dictionary<string, double>(StringComparer.Ordinal) { ["alpha"] = 2.0 });

        Assert.True(((IScoreBoundScorer)prototype).TryGetQueryBound(terms, index, out QueryScoreBound plain));
        Assert.True(weighted.TryGetQueryBound(terms, index, out QueryScoreBound doubled));

        Assert.Equal(2 * plain.UpperBound, doubled.UpperBound, 12);

        double observed = index.Documents
            .Max(document => weighted.Score(document.Id, terms, index));

        Assert.True(doubled.UpperBound + 1e-12 >= observed);
    }

    [Fact]
    public void AnIndexWithoutPostingBounds_MakesTheScorerDecline()
    {
        var index = new NoBoundIndex(Corpus());
        var scorer = (IScoreBoundScorer)new Bm25Scorer();
        var terms = Tokenizer.Default.Tokenize("alpha");

        Assert.False(scorer.TryGetQueryBound(terms, index, out _));
    }

    [Fact]
    public void TheEngineReportsABoundForAPlainQueryAndDeclinesSyntax()
    {
        var index = Corpus();
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        Assert.True(engine.TryGetQueryBound("alpha beta", SearchOptions.Default, out QueryScoreBound plain));
        Assert.True(plain.UpperBound > 0);

        Assert.False(engine.TryGetQueryBound("\"alpha beta\"", SearchOptions.Default, out _));
        Assert.False(engine.TryGetQueryBound("alpha*", SearchOptions.Default, out _));
        Assert.False(engine.TryGetQueryBound("alpha~", SearchOptions.Default, out _));
        Assert.False(engine.TryGetQueryBound("+alpha", SearchOptions.Default, out _));
        Assert.False(engine.TryGetQueryBound("-alpha", SearchOptions.Default, out _));

        // A literal search does not read the operators, so the same text is a plain query there.
        Assert.True(engine.TryGetQueryBound(
            "alpha*",
            new SearchOptions(ParseQuerySyntax: false),
            out _));
    }

    [Fact]
    public void TheSegment_RefusesTheEmptyTerm()
    {
        var segment = new SegmentTextIndex(SegmentWriter.Write(Corpus()));

        // An empty byte string compared against the first dictionary entry is equal to it, so an
        // unguarded lookup would return that term's bound and frequency.
        Assert.False(segment.TryGetTermFrequencyBound("", out _));
        Assert.Equal(0, segment.DocumentFrequency(""));
    }

    [Fact]
    public void TheEngineBound_DoesNotAddSynonymsWhenTheQueryLanguageIsOff()
    {
        var index = new InMemoryTextIndex();
        index.Index(
        [
            new SearchDocument("a", "car"),
            new SearchDocument("b", "automobile automobile automobile"),
        ]);

        var literal = new RankedTextSearchEngine(index, new Bm25Scorer());
        var synonyms = new RankedTextSearchEngine(
            index,
            new Bm25Scorer(),
            synonyms: new SynonymMap().Add("car", "automobile"));

        var literalOptions = new SearchOptions(ParseQuerySyntax: false);

        Assert.True(literal.TryGetQueryBound("car", literalOptions, out QueryScoreBound withoutSynonyms));
        Assert.True(synonyms.TryGetQueryBound("car", literalOptions, out QueryScoreBound withSynonymsOff));

        // The literal path never applies the synonym map, so the two bounds must be the same query.
        Assert.Equal(withoutSynonyms.UpperBound, withSynonymsOff.UpperBound, 12);

        // Parsed, the query does gain the synonym, so its bound is the wider one.
        Assert.True(synonyms.TryGetQueryBound("car", SearchOptions.Default, out QueryScoreBound parsed));
        Assert.True(parsed.UpperBound > withoutSynonyms.UpperBound);
    }

    [Fact]
    public void TheEngineBound_CoversTheSynonymsTheSearchAdds()
    {
        var index = new InMemoryTextIndex();
        index.Index(
        [
            new SearchDocument("a", "car"),
            new SearchDocument("b", "automobile"),
        ]);

        var engine = new RankedTextSearchEngine(
            index,
            new Bm25Scorer(),
            synonyms: new SynonymMap().Add("car", "automobile"));

        Assert.True(engine.TryGetQueryBound("car", SearchOptions.Default, out QueryScoreBound bound));

        var results = engine.Search("car", new SearchOptions(Limit: index.Count));

        Assert.Contains(results, result => result.DocumentId == "b");
        Assert.All(results, result => Assert.True(result.Score <= bound.UpperBound + 1e-12));
    }

    [Fact]
    public void TheEngineBoundCoversEveryReturnedScore()
    {
        var index = Corpus();
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer(1.2, 0.7));

        Assert.True(engine.TryGetQueryBound("alpha beta", SearchOptions.Default, out QueryScoreBound bound));

        var results = engine.Search("alpha beta", new SearchOptions(Limit: index.Count));

        Assert.NotEmpty(results);
        Assert.All(results, result => Assert.True(result.Score <= bound.UpperBound + 1e-12));
    }

    [Fact]
    public void AnEmptyCorpus_BoundsAtZeroWithoutDeclining()
    {
        var index = new InMemoryTextIndex();
        var scorer = (IScoreBoundScorer)new Bm25Scorer();

        Assert.True(scorer.TryGetQueryBound(["alpha"], index, out QueryScoreBound bound));
        Assert.Equal(0, bound.UpperBound);
        Assert.Empty(bound.Terms);
    }

    [Fact]
    public void TheInMemoryIndex_ReportsTheLargestFrequencyAndSmallestLength()
    {
        var index = Corpus();

        Assert.True(index.TryGetTermFrequencyBound("alpha", out TermFrequencyBound bound));

        // "c" holds alpha five times in ten tokens; "a" holds it once in three, the shortest
        // document carrying the term under the default tokenizer.
        Assert.Equal(5, bound.MaxFrequency);
        Assert.Equal(3, bound.MinDocumentLength);

        Assert.False(index.TryGetTermFrequencyBound("quokka", out _));
    }

    [Fact]
    public void TheSegment_StoresTheSameBoundAsTheIndexItWasWrittenFrom()
    {
        var index = Corpus();
        var reader = new SegmentReader(SegmentWriter.Write(index));
        var segment = new SegmentTextIndex(SegmentWriter.Write(index));

        foreach (string term in ((IVocabularyIndex)index).Vocabulary)
        {
            Assert.True(index.TryGetTermFrequencyBound(term, out TermFrequencyBound memory), term);
            Assert.True(reader.TryGetTermBound(term, out TermFrequencyBound stored), term);
            Assert.True(segment.TryGetTermFrequencyBound(term, out TermFrequencyBound read), term);

            Assert.Equal(memory, stored);
            Assert.Equal(memory, read);
        }

        Assert.False(segment.TryGetTermFrequencyBound("quokka", out _));
    }

    [Fact]
    public void TheSegmentBound_CoversTheSegmentScores()
    {
        var index = Corpus();
        var segment = new SegmentTextIndex(SegmentWriter.Write(index));
        var engine = new RankedTextSearchEngine(segment, new Bm25Scorer());

        Assert.True(engine.TryGetQueryBound("alpha beta", SearchOptions.Default, out QueryScoreBound bound));

        var results = engine.Search("alpha beta", new SearchOptions(Limit: segment.Count));

        Assert.NotEmpty(results);
        Assert.All(results, result => Assert.True(result.Score <= bound.UpperBound + 1e-12));
    }

    [Fact]
    public void TheChain_CombinesSegmentsAndTheOverlay()
    {
        var first = new SegmentTextIndex(SegmentWriter.Write(Corpus()));
        var second = new SegmentTextIndex(SegmentWriter.Write(
            new InMemoryTextIndex().With(
                new SearchDocument("g", "alpha alpha alpha alpha alpha alpha"))));

        var chain = new SegmentChain([first, second]);
        chain.Add(new SearchDocument("h", "alpha alpha alpha alpha alpha alpha alpha"));

        Assert.True(chain.TryGetTermFrequencyBound("alpha", out TermFrequencyBound bound));
        Assert.Equal(7, bound.MaxFrequency);
        Assert.Equal(3, bound.MinDocumentLength);

        var engine = new RankedTextSearchEngine(chain, new Bm25Scorer());
        Assert.True(engine.TryGetQueryBound("alpha", SearchOptions.Default, out QueryScoreBound query));

        var results = engine.Search("alpha", new SearchOptions(Limit: chain.Count));
        Assert.All(results, result => Assert.True(result.Score <= query.UpperBound + 1e-12));
    }

    /// <summary>An index that answers everything except the posting bound, for the decline path.</summary>
    private sealed class NoBoundIndex(IReadOnlyTextIndex inner) : IReadOnlyTextIndex
    {
        public IReadOnlyCollection<SearchDocument> Documents => inner.Documents;
        public int Count => inner.Count;
        public double AverageDocumentLength => inner.AverageDocumentLength;
        public int StatisticDocumentCount => inner.StatisticDocumentCount;
        public int VocabularySize => inner.VocabularySize;
        public long CorpusTokenCount => inner.CorpusTokenCount;
        public IReadOnlyCollection<string> Fields => inner.Fields;
        public bool Contains(string documentId) => inner.Contains(documentId);
        public IReadOnlyList<string> GetTerms(string documentId) => inner.GetTerms(documentId);
        public IReadOnlyList<int> GetTermPositions(string documentId, string term) => inner.GetTermPositions(documentId, term);
        public int DocumentFrequency(string term) => inner.DocumentFrequency(term);
        public int CorpusFrequency(string term) => inner.CorpusFrequency(term);
        public int TermFrequency(string documentId, string term) => inner.TermFrequency(documentId, term);
        public int DocumentLength(string documentId) => inner.DocumentLength(documentId);
        public bool TryGetDocument(
            string documentId,
            [NotNullWhen(true)] out SearchDocument? document) =>
            inner.TryGetDocument(documentId, out document);
    }
}

/// <summary>A tiny fluent helper so a one-document corpus reads as one expression.</summary>
internal static class IndexTestExtensions
{
    public static InMemoryTextIndex With(this InMemoryTextIndex index, params SearchDocument[] documents)
    {
        index.Index(documents);

        return index;
    }
}
