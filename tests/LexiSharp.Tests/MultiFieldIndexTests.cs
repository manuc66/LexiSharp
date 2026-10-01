using System.Diagnostics.CodeAnalysis;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Covers the per-field statistics added to <see cref="InMemoryTextIndex"/>, and the contract the
/// rest of the library relies on: the flat (existing) members are the union of every field, so a
/// single-field index is unchanged and a field-aware scorer can weight fields.
/// </summary>
public class MultiFieldIndexTests
{
    private static InMemoryTextIndex Create(params SearchDocument[] documents)
    {
        var index = new InMemoryTextIndex();
        index.Index(documents);
        return index;
    }

    /// <summary>
    /// One document with a title and a body — the shape field-aware scoring exists for. A
    /// summary is only added when given, so a document can carry one field or two.
    /// </summary>
    private static SearchDocument Article(
        string id,
        string title,
        string body,
        string? summary = null)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = title };

        if (summary is not null)
            fields["summary"] = summary;

        return new SearchDocument(id, body, TextFields: fields);
    }

    // ---- the flat view is the union of every field ---------------------------------------------

    [Fact]
    public void FieldTermsAreReachableThroughTheFlatStatistics()
    {
        var index = Create(Article("1", "red title", "blue body", "green summary"));

        // 'red' exists only in a TextField. The flat view must see it or the document is
        // unreachable: no candidate, no score.
        Assert.Equal(1, index.TermFrequency("1", "red"));
        Assert.Equal(1, index.DocumentFrequency("red"));
        Assert.Contains("red", index.GetTerms("1"));
    }

    [Fact]
    public void FlatDocumentLengthCountsEveryField()
    {
        var index = Create(Article("1", "red title", "blue body", "green summary"));

        // default field "blue body" = 2, "red title" = 2, "green summary" = 2.
        Assert.Equal(6, index.DocumentLength("1"));
        Assert.Equal(6, index.AverageDocumentLength);
    }

    [Fact]
    public void AFieldOnlyMatchIsFoundByAFieldUnawareEngine()
    {
        var index = Create(Article("1", "obsidian", "nothing here"));
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        var results = engine.Search("obsidian");

        var hit = Assert.Single(results);
        Assert.Equal("1", hit.DocumentId);
        Assert.True(hit.Score > 0);
    }

    // ---- per-field statistics ------------------------------------------------------------------

    [Fact]
    public void PerFieldStatisticsAreReportedPerField()
    {
        var index = Create(
            Article("1", "red title", "blue body", "green summary"),
            Article("2", "red other", "blue body", "yellow summary"));

        Assert.Equal(1, index.FieldTermFrequency("1", "title", "red"));
        Assert.Equal(1, index.FieldTermFrequency("1", TextFields.Default, "blue"));
        Assert.Equal(1, index.FieldTermFrequency("1", "summary", "green"));
        Assert.Equal(0, index.FieldTermFrequency("1", "summary", "red"));

        // "blue body" holds 'blue' once, so the default field's own count is 1 even though the
        // flat count across every field is 1 too. The flat view would differ if the term appeared
        // in two fields of the same document.
        Assert.Equal(1, index.FieldTermFrequency("1", TextFields.Default, "blue"));
        Assert.Equal(2, index.FieldLength("1", TextFields.Default));
        Assert.Equal(2, index.FieldLength("1", "title"));
    }

    [Fact]
    public void FieldDocumentFrequencyCountsThatFieldNotTheCorpus()
    {
        var index = Create(
            Article("1", "red title", "blue body"),
            Article("2", "red other", "blue body"));

        // 'red' is in both titles, 'blue' in both bodies: the flat and per-field counts agree here.
        Assert.Equal(2, index.FieldDocumentFrequency("title", "red"));
        Assert.Equal(2, index.DocumentFrequency("red"));

        // 'body' is in neither title, so the per-field count is 0 while the flat count is 2.
        Assert.Equal(0, index.FieldDocumentFrequency("title", "body"));
        Assert.Equal(2, index.DocumentFrequency("body"));
    }

    [Fact]
    public void AverageFieldLengthAveragesOverTheDocumentsCarryingTheField()
    {
        // Multi-character, non-stop words: the default tokenizer drops one-character terms and
        // English stop words, so 'a b' or 'no title here' would not tokenize the way it reads.
        var index = Create(
            Article("1", "alpha beta", "gamma", "delta epsilon"),
            Article("2", "zeta", "gamma", "eta theta iota"));

        // title: 2 and 1 tokens over 2 documents. summary: 2 and 3. default: 1 and 1.
        Assert.Equal(1.5, index.AverageFieldLength("title"), 6);
        Assert.Equal(2.5, index.AverageFieldLength("summary"), 6);
        Assert.Equal(1.0, index.AverageFieldLength(TextFields.Default), 6);
    }

    [Fact]
    public void AFieldNobodyCarriesAveragesToZeroRatherThanThrowing()
    {
        var index = Create(Article("1", "red title", "blue body"));

        Assert.Equal(0, index.AverageFieldLength("never-used"));
        Assert.Equal(0, index.FieldLength("1", "never-used"));
        Assert.Equal(0, index.FieldTermFrequency("1", "never-used", "red"));
    }

    [Fact]
    public void AFieldPresentButEmptyIsCarriedAndAveragesIn()
    {
        var index = Create(Article("1", "", "blue body"));

        Assert.Contains("title", index.Fields);
        Assert.Equal(0, index.FieldLength("1", "title"));
        Assert.Equal(0, index.AverageFieldLength("title"), 6);
    }

    // ---- Fields ---------------------------------------------------------------------------------

    [Fact]
    public void ASingleFieldIndexReportsOnlyTheDefaultField()
    {
        var index = Create(new SearchDocument("1", "red blue"));

        Assert.Equal([TextFields.Default], index.Fields);
        Assert.IsAssignableFrom<IFieldStatisticsIndex>(index);
    }

    [Fact]
    public void FieldsAreReportedDefaultFirstThenInOrdinalOrder()
    {
        var index = Create(new SearchDocument(
            "1",
            "body",
            TextFields: new Dictionary<string, string>
            {
                ["zebra"] = "z",
                ["author"] = "a",
                ["title"] = "t",
            }));

        Assert.Equal([TextFields.Default, "author", "title", "zebra"], index.Fields);
    }

    // ---- phrase safety ---------------------------------------------------------------------------

    [Fact]
    public void APhraseMatchesInsideOneField()
    {
        var index = Create(Article("1", "red title", "blue body", "red summary"));
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        var results = engine.Search("\"red summary\"");

        Assert.Equal("1", Assert.Single(results).DocumentId);
    }

    [Fact]
    public void APhraseNeverBridgesTheMainTextAndAField()
    {
        // The main text ends with 'body'; the title field is 'red'. Consecutive positions would
        // make "body red" match if the fields shared a position run.
        var index = Create(new SearchDocument(
            "1",
            "blue body",
            TextFields: new Dictionary<string, string> { ["title"] = "red" }));

        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        Assert.Empty(engine.Search("\"body red\""));
    }

    [Fact]
    public void APhraseNeverBridgesTwoFields()
    {
        var index = Create(new SearchDocument(
            "1",
            "blue",
            TextFields: new Dictionary<string, string>
            {
                ["title"] = "red",
                ["summary"] = "green",
            }));

        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        Assert.Empty(engine.Search("\"red green\""));
        Assert.Equal("1", Assert.Single(engine.Search("\"red\"")).DocumentId);
    }

    // ---- backward compatibility ------------------------------------------------------------------

    [Fact]
    public void ADocumentWithoutTextFieldsBehavesExactlyAsBefore()
    {
        // Same document indexed with and without an empty TextFields dictionary must produce the
        // same statistics, the same positions and the same score.
        var plain = Create(new SearchDocument("1", "red blue red"));
        var withEmpty = Create(new SearchDocument(
            "1", "red blue red", TextFields: new Dictionary<string, string>()));

        Assert.Equal(plain.DocumentLength("1"), withEmpty.DocumentLength("1"));
        Assert.Equal(plain.AverageDocumentLength, withEmpty.AverageDocumentLength);
        Assert.Equal(plain.TermFrequency("1", "red"), withEmpty.TermFrequency("1", "red"));
        Assert.Equal(plain.GetTerms("1"), withEmpty.GetTerms("1"));
        Assert.Equal(plain.GetTermPositions("1", "red"), withEmpty.GetTermPositions("1", "red"));
        Assert.Equal(plain.CorpusFrequency("red"), withEmpty.CorpusFrequency("red"));
        Assert.Equal(plain.VocabularySize, withEmpty.VocabularySize);

        Assert.Equal(
            new Bm25Scorer().Score("1", ["red", "blue"], plain),
            new Bm25Scorer().Score("1", ["red", "blue"], withEmpty),
            12);
    }

    [Fact]
    public void ReAddingADocumentReplacesItsFieldStatisticsInsteadOfAccumulating()
    {
        var index = Create(Article("1", "red title", "blue body"));
        index.Add(Article("1", "red title", "green body"));

        // The replacement dropped 'blue' and brought in 'green'; the field did not accumulate.
        Assert.Equal(0, index.FieldTermFrequency("1", TextFields.Default, "blue"));
        Assert.Equal(1, index.FieldTermFrequency("1", TextFields.Default, "green"));
        Assert.Equal(2, index.FieldLength("1", TextFields.Default));

        // The flat length is the union: a 2-token body and a 2-token title.
        Assert.Equal(4, index.DocumentLength("1"));
    }

    // ---- mutations keep the per-field statistics consistent ----------------------------------------

    [Fact]
    public void RemoveDropsTheDocumentFromEveryField()
    {
        var index = Create(
            Article("1", "red title", "blue body"),
            Article("2", "red other", "green body"));

        Assert.True(index.Remove("1"));

        Assert.Equal(1, index.FieldDocumentFrequency("title", "red"));
        Assert.Equal(0, index.FieldDocumentFrequency(TextFields.Default, "blue"));
        Assert.Equal(1, index.FieldDocumentFrequency(TextFields.Default, "green"));
        Assert.Equal(1, index.FieldTermFrequency("2", TextFields.Default, "green"));
        Assert.Equal(2.0, index.AverageFieldLength(TextFields.Default), 6);
    }

    [Fact]
    public void RemovingTheLastDocumentCarryingAFieldForgetsTheField()
    {
        // The second title is made of single-character terms, which the default tokenizer drops,
        // so that document declares a title field that tokenizes to nothing.
        var index = Create(
            Article("1", "red title", "blue body"),
            Article("2", "a b", "green body"));

        Assert.Contains("title", index.Fields);
        Assert.Equal(0, index.FieldLength("2", "title"));

        // (2 + 0) over the 2 documents that declare the field — the empty one still counts.
        Assert.Equal(1.0, index.AverageFieldLength("title"), 6);

        // Dropping the empty carrier keeps the field, now averaged over its one real document.
        index.Remove("2");

        Assert.Contains("title", index.Fields);
        Assert.Equal(2.0, index.AverageFieldLength("title"), 6);
        Assert.Equal(2.0, index.AverageFieldLength(TextFields.Default), 6);

        // Dropping the last document that declares it forgets the field entirely, rather than
        // leaving a stale name in Fields.
        index.Remove("1");

        Assert.DoesNotContain("title", index.Fields);
        Assert.Equal(0, index.AverageFieldLength("title"), 6);
    }

    [Fact]
    public void ClearResetsTheFieldStatistics()
    {
        var index = Create(Article("1", "red title", "blue body"));

        index.Clear();

        Assert.Equal([TextFields.Default], index.Fields);
        Assert.Equal(0, index.FieldTermFrequency("1", "title", "red"));
        Assert.Equal(0, index.AverageFieldLength("title"));
    }

    [Fact]
    public void IncrementalAddAndRemoveLeaveTheCorpusAveragesRight()
    {
        var index = Create(Article("1", "alpha beta", "gamma"));
        Assert.Equal(2.0, index.AverageFieldLength("title"), 6);

        index.Add(Article("2", "alpha beta gamma delta", "gamma"));
        Assert.Equal(3.0, index.AverageFieldLength("title"), 6);

        index.Remove("1");
        Assert.Equal(4.0, index.AverageFieldLength("title"), 6);
    }

    // ---- argument validation ----------------------------------------------------------------------

    [Fact]
    public void ABlankFieldNameIsRejectedAtIndexTime()
    {
        var index = Create();

        var exception = Assert.Throws<ArgumentException>(() => index.Add(new SearchDocument(
            "1",
            "body",
            TextFields: new Dictionary<string, string> { ["   "] = "x" })));

        Assert.Equal("TextFields", exception.ParamName);
    }

    [Fact]
    public void ABlankFieldNameIsRejectedOnQuery()
    {
        var index = Create(Article("1", "red title", "blue body"));

        Assert.Throws<ArgumentException>(() => index.FieldLength("1", " "));
        Assert.Throws<ArgumentNullException>(() => index.FieldTermFrequency("1", "title", null!));

        // The default field IS the empty string, so the validator has to accept it.
        Assert.Equal(2, index.FieldLength("1", TextFields.Default));
        Assert.Equal(2.0, index.AverageFieldLength(TextFields.Default), 6);
    }

    // ---- the contract for an index that does not track fields --------------------------------------

    [Fact]
    public void AnIndexWithoutFieldStatisticsSaysSoInsteadOfReturningZero()
    {
        ITextIndex index = new NoFieldStatisticsIndex();

        // A field-blind index is not an IFieldStatisticsIndex, so a field-aware scorer refuses it
        // by name (see Bm25FScorerTests / Bm25FParameterTunerTests) instead of reading a silent
        // zero — a zero here would make a field-weighted ranking quietly wrong.
        Assert.IsNotAssignableFrom<IFieldStatisticsIndex>(index);
        Assert.Equal([TextFields.Default], index.Fields);
    }

    private sealed class NoFieldStatisticsIndex : ITextIndex
    {
        public IReadOnlyCollection<SearchDocument> Documents => Array.Empty<SearchDocument>();

        public int Count => 0;

        public double AverageDocumentLength => 0;

        public int VocabularySize => 0;

        public long CorpusTokenCount => 0;

        public void Index(IEnumerable<SearchDocument> documents)
        {
        }

        public void Add(SearchDocument document)
        {
        }

        public bool Remove(string documentId) => false;

        public void Clear()
        {
        }

        public bool Contains(string documentId) => false;

        public IReadOnlyList<string> GetTerms(string documentId) => Array.Empty<string>();

        public IReadOnlyList<int> GetTermPositions(string documentId, string term) =>
            Array.Empty<int>();

        public int DocumentFrequency(string term) => 0;

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
