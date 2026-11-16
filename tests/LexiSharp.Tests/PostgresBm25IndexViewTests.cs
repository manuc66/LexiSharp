using LexiSharp.Core;
using LexiSharp.Postgres;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The candidate pool as an <see cref="IReadOnlyTextIndex"/>: the members <see cref="Ranking.Bm25Scorer"/>
/// reads, and the ones it does not, which are pinned here so a future caller finds the stated
/// answer rather than an accidental one.
/// </summary>
public class PostgresBm25IndexViewTests
{
    private static PostgresBm25Statistics Statistics() =>
        PostgresBm25Statistics.From(
            10,
            new Dictionary<string, (int DocumentFrequency, int CorpusFrequency)>(StringComparer.Ordinal)
            {
                ["quick"] = (3, 5),
                ["brown"] = (2, 2),
            });

    private static PostgresBm25IndexView View() =>
        new(Statistics(),
        [
            new PostgresBm25Candidate(
                "d1", new Dictionary<string, int>(StringComparer.Ordinal) { ["quick"] = 2 }, 4),
            new PostgresBm25Candidate(
                "d2", new Dictionary<string, int>(StringComparer.Ordinal) { ["brown"] = 1 }, 2),
        ]);

    [Fact]
    public void Count_IsTheCandidateCount() => Assert.Equal(2, View().Count);

    [Fact]
    public void CorpusStatistics_ComeFromTheSnapshot()
    {
        PostgresBm25IndexView view = View();

        Assert.Equal(10, view.StatisticDocumentCount);
        Assert.Equal(2, view.VocabularySize);
        Assert.Equal(7, view.CorpusTokenCount);
        Assert.Equal(0.7, view.AverageDocumentLength, precision: 10);
    }

    [Fact]
    public void DocumentFrequency_ComesFromTheSnapshot()
    {
        PostgresBm25IndexView view = View();

        Assert.Equal(3, view.DocumentFrequency("quick"));
        Assert.Equal(0, view.DocumentFrequency("absent"));
        Assert.Equal(0, view.DocumentFrequency(null!));
    }

    [Fact]
    public void CorpusFrequency_ComesFromTheSnapshot()
    {
        PostgresBm25IndexView view = View();

        Assert.Equal(5, view.CorpusFrequency("quick"));
        Assert.Equal(0, view.CorpusFrequency("absent"));
        Assert.Equal(0, view.CorpusFrequency(null!));
    }

    [Fact]
    public void PerDocumentAnswers_ComeFromTheCandidate()
    {
        PostgresBm25IndexView view = View();

        Assert.Equal(2, view.TermFrequency("d1", "quick"));
        Assert.Equal(0, view.TermFrequency("d1", "brown"));
        Assert.Equal(0, view.TermFrequency("d1", null!));
        Assert.Equal(4, view.DocumentLength("d1"));
        Assert.True(view.Contains("d1"));
        Assert.False(view.Contains("absent"));
        Assert.False(view.Contains(null!));
    }

    [Fact]
    public void GetTerms_IsOrdinalSorted()
    {
        var view = new PostgresBm25IndexView(
            Statistics(),
            [
                new PostgresBm25Candidate(
                    "d1",
                    new Dictionary<string, int>(StringComparer.Ordinal) { ["b"] = 1, ["a"] = 1, ["C"] = 1 },
                    3),
            ]);

        Assert.Equal(["C", "a", "b"], view.GetTerms("d1"));
    }

    [Fact]
    public void UnknownDocument_Throws()
    {
        PostgresBm25IndexView view = View();

        Assert.Throws<ArgumentException>(() => view.TermFrequency("absent", "quick"));
        Assert.Throws<ArgumentException>(() => view.DocumentLength("absent"));
        Assert.Throws<ArgumentException>(() => view.GetTerms("absent"));
    }

    [Fact]
    public void ThePoolKeepsNoDocumentsAndNoPositions()
    {
        PostgresBm25IndexView view = View();

        Assert.Throws<NotSupportedException>(() => view.Documents);
        Assert.Throws<NotSupportedException>(() => view.GetTermPositions("d1", "quick"));
        Assert.False(view.TryGetDocument("d1", out _));
    }

    [Fact]
    public void AnEmptySnapshot_HasNoAverageLength()
    {
        PostgresBm25Statistics statistics = PostgresBm25Statistics.From(
            0, new Dictionary<string, (int, int)>());

        Assert.Equal(0, statistics.AverageDocumentLength);
        Assert.Equal(0, statistics.VocabularySize);
        Assert.Equal(0, statistics.CorpusTokenCount);
    }
}
