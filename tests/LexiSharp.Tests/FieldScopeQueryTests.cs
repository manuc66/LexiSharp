using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Field-scoped query atoms through the public search surface: <c>title:term</c>,
/// <c>title:"a phrase"</c>, their <c>+</c>/<c>-</c> forms, and the promise that a colon which does
/// not name a field stays ordinary text.
/// </summary>
public class FieldScopeQueryTests
{
    /// <summary>
    /// Three documents on a two-field corpus (body plus <c>title</c>), chosen so a scoped match and
    /// a body match are distinguishable: <c>obsidian</c> is in one title only, one body only, and
    /// both.
    /// </summary>
    private static InMemoryTextIndex Index()
    {
        var index = new InMemoryTextIndex();

        index.Index(
        [
            new SearchDocument("title-hit", "unrelated body text",
                TextFields: new Dictionary<string, string> { ["title"] = "obsidian guide" }),
            new SearchDocument("body-hit", "an obsidian guide in the body",
                TextFields: new Dictionary<string, string> { ["title"] = "unrelated heading" }),
            new SearchDocument("both-hit", "obsidian in the body",
                TextFields: new Dictionary<string, string> { ["title"] = "obsidian notes" }),
        ]);

        return index;
    }

    private static RankedTextSearchEngine Engine(InMemoryTextIndex index, ITextScorer? scorer = null) =>
        new(index, scorer ?? new Bm25FScorer(), Tokenizer.Default);

    private static string[] Ids(IReadOnlyList<SearchResult> results) =>
        results.Select(result => result.DocumentId).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void ScopedTerm_ReturnsOnlyTheDocumentsWithTheTermInThatField()
    {
        var engine = Engine(Index());

        // body-hit contains "obsidian" too, and is excluded: the scope asked for the title.
        Assert.Equal(
            new[] { "both-hit", "title-hit" },
            Ids(engine.Search("title:obsidian", new SearchOptions(Limit: 10))));

        // The same term unscoped reaches every document that carries it anywhere.
        Assert.Equal(
            new[] { "body-hit", "both-hit", "title-hit" },
            Ids(engine.Search("obsidian", new SearchOptions(Limit: 10))));
    }

    [Fact]
    public void ScopedTerm_ScoresFromFieldStatisticsNotWholeDocumentLength()
    {
        // Same title, very different bodies. A field-scoped term has to score these equally: the
        // evidence is the title, and the body's length is not part of that evidence.
        var index = new InMemoryTextIndex();
        index.Index(
        [
            new SearchDocument("short-body", "one two",
                TextFields: new Dictionary<string, string> { ["title"] = "obsidian" }),
            new SearchDocument("long-body",
                "one two three four five six seven eight nine ten eleven twelve thirteen fourteen",
                TextFields: new Dictionary<string, string> { ["title"] = "obsidian" }),
        ]);

        var results = Engine(index).Search("title:obsidian", new SearchOptions(Limit: 10));

        Assert.Equal(2, results.Count);
        Assert.Equal(results[0].Score, results[1].Score);
    }

    [Fact]
    public void ScopedPhrase_MatchesInsideItsFieldAndNotAcrossTheBoundary()
    {
        var index = new InMemoryTextIndex();
        index.Index(
        [
            new SearchDocument("in-title", "nothing here",
                TextFields: new Dictionary<string, string> { ["title"] = "red apple" }),
            // Both words are in the title, but not consecutively; the body has the phrase. A
            // whole-document position check would accept this document for `title:"red apple"`.
            new SearchDocument("in-body-only", "red apple",
                TextFields: new Dictionary<string, string> { ["title"] = "red giant apple" }),
        ]);

        var engine = Engine(index);

        Assert.Equal(
            new[] { "in-title" },
            Ids(engine.Search("title:\"red apple\"", new SearchOptions(Limit: 10))));

        // The unscoped phrase matches the body of the second document, as it always did.
        Assert.Equal(
            new[] { "in-body-only", "in-title" },
            Ids(engine.Search("\"red apple\"", new SearchOptions(Limit: 10))));
    }

    [Fact]
    public void RequiredAndProhibitedScopedAtoms_GateOnTheField()
    {
        var index = new InMemoryTextIndex();
        index.Index(
        [
            new SearchDocument("title-hit", "apple body",
                TextFields: new Dictionary<string, string> { ["title"] = "apple" }),
            new SearchDocument("body-hit", "apple body",
                TextFields: new Dictionary<string, string> { ["title"] = "pear" }),
        ]);

        var engine = Engine(index);

        // +title:apple requires it in the title, so the body-only document is out.
        Assert.Equal(
            new[] { "title-hit" },
            Ids(engine.Search("+title:apple", new SearchOptions(Limit: 10))));

        // -title:apple excludes only the document whose title carries it; the body match survives.
        Assert.Equal(
            new[] { "body-hit" },
            Ids(engine.Search("apple -title:apple", new SearchOptions(Limit: 10))));

        // And the prohibited-only query is the complement of its field gate.
        Assert.Equal(
            new[] { "body-hit" },
            Ids(engine.Search("-title:apple", new SearchOptions(Limit: 10))));
    }

    [Fact]
    public void ProhibitedScopedPhrase_ExcludesOnlyTheFieldOccurrence()
    {
        var index = new InMemoryTextIndex();
        index.Index(
        [
            new SearchDocument("in-title", "nothing",
                TextFields: new Dictionary<string, string> { ["title"] = "red apple" }),
            new SearchDocument("in-body", "red apple",
                TextFields: new Dictionary<string, string> { ["title"] = "red giant apple" }),
        ]);

        Assert.Equal(
            new[] { "in-body" },
            Ids(Engine(index).Search("-title:\"red apple\"", new SearchOptions(Limit: 10))));
    }

    [Fact]
    public void AMixedQueryRanksTheTitleMatchAboveTheBodyMatch()
    {
        var index = new InMemoryTextIndex();
        index.Index(
        [
            new SearchDocument("title-match", "notes about rocks",
                TextFields: new Dictionary<string, string> { ["title"] = "obsidian" }),
            new SearchDocument("body-match", "obsidian notes",
                TextFields: new Dictionary<string, string> { ["title"] = "unrelated" }),
        ]);

        var results = Engine(index).Search("title:obsidian notes", new SearchOptions(Limit: 10));

        // Both documents match the free term; only the first matches the scoped one, and the
        // scoped contribution is what puts it first.
        Assert.Equal(new[] { "title-match", "body-match" }, results.Select(r => r.DocumentId));
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public void ScopedExpansion_ResolvesAndGatesInTheField()
    {
        var engine = Engine(Index());

        Assert.Equal(
            new[] { "both-hit", "title-hit" },
            Ids(engine.Search("title:obsid*", new SearchOptions(Limit: 10))));

        Assert.Equal(
            new[] { "body-hit" },
            Ids(engine.Search("-title:obsid*", new SearchOptions(Limit: 10))));
    }

    [Fact]
    public void UnknownField_FailsLoudlyInsteadOfReturningNothing()
    {
        // The caller declared a name the index does not have. Treating it as text would search
        // "titel" and "obsidian"; returning zero results would hide the typo.
        var engine = Engine(Index());

        var options = new SearchOptions(Limit: 10)
        {
            KnownFields = new HashSet<string>(["titel"], StringComparer.Ordinal),
        };

        var exception = Assert.Throws<NotSupportedException>(() => engine.Search("titel:obsidian", options));

        Assert.Contains("titel", exception.Message, StringComparison.Ordinal);
        Assert.Contains("title", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AScorerWithoutTheCapability_RefusesTheQuery()
    {
        var engine = Engine(Index(), new Bm25Scorer());

        var exception = Assert.Throws<NotSupportedException>(
            () => engine.Search("title:obsidian", new SearchOptions(Limit: 10)));

        Assert.Contains("field-scoped", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IFieldScopedScorer), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseQuerySyntaxFalse_LeavesAColonAsOrdinaryText()
    {
        var engine = Engine(Index(), new Bm25Scorer());

        // "title" is not a term in the corpus, so the literal reading of "title:obsidian" is just
        // "obsidian", matched anywhere.
        var results = engine.Search(
            "title:obsidian", new SearchOptions(Limit: 10, ParseQuerySyntax: false));

        Assert.Equal(new[] { "body-hit", "both-hit", "title-hit" }, Ids(results));
    }

    [Fact]
    public void ColonsThatDoNotNameAField_AreOrdinaryText()
    {
        var index = new InMemoryTextIndex();
        index.Index(
        [
            new SearchDocument("url", "http example page",
                TextFields: new Dictionary<string, string> { ["title"] = "docs" }),
            new SearchDocument("time", "the 12 30 train",
                TextFields: new Dictionary<string, string> { ["title"] = "timetable" }),
            new SearchDocument("pair", "key value pair",
                TextFields: new Dictionary<string, string> { ["title"] = "glossary" }),
        ]);

        var engine = Engine(index);

        // `http`, `12` and `key` are not fields of this index, so each query is the same terms the
        // tokenizer produces — `key:value` is not a scope on a field called `key`.
        Assert.Equal(
            Ids(engine.Search("http example", new SearchOptions(Limit: 10))),
            Ids(engine.Search("http://example.page", new SearchOptions(Limit: 10))));

        Assert.Equal(
            Ids(engine.Search("12 30 train", new SearchOptions(Limit: 10))),
            Ids(engine.Search("12:30 train", new SearchOptions(Limit: 10))));

        Assert.Equal(
            Ids(engine.Search("key value pair", new SearchOptions(Limit: 10))),
            Ids(engine.Search("key:value pair", new SearchOptions(Limit: 10))));
    }

    [Fact]
    public void DeclaringTheColonNameAsAFieldWouldMakeItAScope()
    {
        // The rule is "a known field name", not "a word before a colon": a corpus that really has
        // a `key` field reads `key:value` as a scope. Stated here so the behaviour is a decision
        // rather than a surprise.
        var index = new InMemoryTextIndex();
        index.Index(
        [
            new SearchDocument("pair", "value anywhere",
                TextFields: new Dictionary<string, string> { ["key"] = "value" }),
        ]);

        var engine = Engine(index);

        Assert.Equal(
            new[] { "pair" },
            Ids(engine.Search("key:value", new SearchOptions(Limit: 10))));

        // A scoped phrase that the field does not contain matches nothing.
        Assert.Empty(engine.Search("key:\"nothing\"", new SearchOptions(Limit: 10)));
    }

    [Fact]
    public void Explain_RefusesAScopedQueryInsteadOfOmittingItsContribution()
    {
        var engine = Engine(Index());

        var exception = Assert.Throws<NotSupportedException>(
            () => engine.Explain("title-hit", "title:obsidian"));

        Assert.Contains("Explain", exception.Message, StringComparison.Ordinal);
    }
}
