using LexiSharp.Core;
using LexiSharp.Expansion;
using Xunit;

namespace LexiSharp.Tests;

public class LexiSharpIndexTests
{
    private sealed record SampleDoc(string Id, string Body, string Category = "", params string[] Tags);

    private static (string Id, string Body, string Category, string[] Tags) With(
        string id, string body, string category = "", params string[] tags) =>
        (id, body, category, tags);

    private static LexiSharpIndex<SampleDoc> CreateIndex(
        Action<LexiSharpIndexOptions<SampleDoc>>? configure = null,
        params (string Id, string Body, string Category, string[] Tags)[] docs)
    {
        var index = new LexiSharpIndex<SampleDoc>(o =>
        {
            o.Id = d => d.Id;
            o.Text = d => d.Body;
            o.Fields = d => new Dictionary<string, string>
            {
                ["category"] = d.Category,
                ["tags"] = string.Join(",", d.Tags),
            };
            configure?.Invoke(o);
        });

        index.AddRange(docs.Select(d => new SampleDoc(d.Id, d.Body, d.Category, d.Tags)));
        return index;
    }

    private static LexiSharpHit<T> RequireSingle<T>(IReadOnlyList<LexiSharpHit<T>> hits)
        where T : class
    {
        Assert.Single(hits);
        return hits[0];
    }

    [Fact]
    public void TextFieldsSelector_ReachesTheIndexAndStaysSearchable()
    {
        var index = new LexiSharpIndex<SampleDoc>(o =>
        {
            o.Id = d => d.Id;
            o.Text = d => d.Body;
            o.TextFields = d => new Dictionary<string, string> { ["title"] = d.Category };
        });

        index.AddRange(
        [
            new SampleDoc("1", "the article body discusses indexing", "search ranking"),
            new SampleDoc("2", "another body about cooking", "italian cuisine"),
        ]);

        // Per-field statistics are available through the facade's own index...
        Assert.Equal([TextFields.Default, "title"], index.TextIndex.Fields);
        IFieldStatisticsIndex fieldStats = Assert.IsAssignableFrom<IFieldStatisticsIndex>(index.TextIndex);
        Assert.Equal(1, fieldStats.FieldTermFrequency("1", "title", "ranking"));
        Assert.Equal(2, fieldStats.FieldLength("1", "title"));

        // ...and a field-only term is still findable by a plain query, because the flat view is
        // the union of the fields.
        Assert.Equal("1", RequireSingle(index.Search("ranking")).Document.Id);
    }

    [Fact]
    public void TextFields_DefaultToTheDocumentOnesWhenTheTypeIsSearchDocument()
    {
        var index = new LexiSharpIndex<SearchDocument>(o =>
        {
            o.Id = d => d.Id;
            o.Text = d => d.Text;
        });

        index.Add(new SearchDocument(
            "1",
            "the article body",
            TextFields: new Dictionary<string, string> { ["title"] = "search ranking" }));

        IFieldStatisticsIndex fieldStats = Assert.IsAssignableFrom<IFieldStatisticsIndex>(index.TextIndex);
        Assert.Equal(1, fieldStats.FieldTermFrequency("1", "title", "ranking"));
    }

    [Fact]
    public void Search_ReturnsTypedHitsBestFirst()
    {
        var index = CreateIndex(null,
            With("1", "the search engine uses BM25 to rank results", "search"),
            With("2", "BM25 ranking is a classic method of textual search", "search"),
            With("3", "Italian cuisine is renowned in Rome", "other"));

        var hits = index.Search("textual search");

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.NotNull(h.Document));
        Assert.Contains(hits, h => h.Document.Id == "2");
        Assert.Equal("2", hits[0].Document.Id);
        Assert.True(hits[0].Score > 0);
    }

    [Fact]
    public void Search_DefaultMappingOnStrings()
    {
        var index = new LexiSharpIndex<string>();
        index.AddRange(new[] { "quick brown fox", "lazy dog" });

        var hits = index.Search("brown");

        var hit = RequireSingle(hits);
        Assert.Equal("quick brown fox", hit.Document);
        Assert.Equal("quick brown fox", hit.DocumentId);
    }

    [Fact]
    public void Search_DefaultMappingOnSearchDocuments()
    {
        var index = new LexiSharpIndex<SearchDocument>();
        index.Add(new SearchDocument("a", "hello world", new Dictionary<string, string> { ["lang"] = "en" }));

        var hit = RequireSingle(index.Search("hello"));

        Assert.Equal("a", hit.DocumentId);
        Assert.Equal("hello world", hit.Document.Text);
        Assert.Equal("en", hit.Document.Fields!["lang"]);
    }

    [Fact]
    public void MissingSelectors_ThrowOnNonSupportedDocumentType()
    {
        var exception = Assert.Throws<ArgumentException>(() => new LexiSharpIndex<SampleDoc>());
        Assert.Contains("Id", exception.Message);
    }

    [Fact]
    public void Add_ReplacesDocumentWithSameId()
    {
        var index = new LexiSharpIndex<string>();
        index.Add("first text");
        index.Add("second text about potatoes");

        Assert.Single(index.Search("first"));
        var hit = RequireSingle(index.Search("potatoes"));
        Assert.Equal("second text about potatoes", hit.Document);
    }

    [Fact]
    public void Index_ReplacesWholeIndex()
    {
        var index = new LexiSharpIndex<string>();
        index.Add("old corpus");

        index.Index(new[] { "fresh corpus", "another fresh note" });

        Assert.Empty(index.Search("old"));
        Assert.Equal(2, index.Count);
    }

    [Fact]
    public void Remove_And_Clear()
    {
        var index = new LexiSharpIndex<string>();
        index.AddRange(new[] { "first", "second" });

        index.Remove("first");
        Assert.Empty(index.Search("first"));
        Assert.Equal(1, index.Count);

        index.Clear();
        Assert.Equal(0, index.Count);
        Assert.Empty(index.Search("second"));
    }

    [Fact]
    public void Search_HonorsLimitAndOffset()
    {
        var index = new LexiSharpIndex<string>();
        index.AddRange(new[] { "alpha AA", "alpha BB", "alpha CC" });

        var page = index.Search("alpha", new LexiSharpQueryOptions(Limit: 2, Offset: 1));

        Assert.Equal(2, page.Count);
        Assert.Equal("alpha BB", page[0].Document);
        Assert.Equal("alpha CC", page[1].Document);
    }

    [Fact]
    public void SearchWithFacets_ReturnsBucketsOverMatchSet()
    {
        var index = CreateIndex(null,
            With("1", "recipe pasta", "cooking", "pasta"),
            With("2", "recipe steak", "cooking", "steak"),
            With("3", "recipe salad", "health", "salad"));

        var result = index.SearchWithFacets("recipe", facetFields: new[] { "category" });

        Assert.Equal(3, result.Results.Count);
        Assert.Single(result.Buckets);
        Assert.Equal("category", result.Buckets[0].Field);
        Assert.Equal(2, result.Buckets[0].Values.Single(v => v.Value == "cooking").Count);
        Assert.Equal(1, result.Buckets[0].Values.Single(v => v.Value == "health").Count);
    }

    [Fact]
    public void Explain_ReturnsTermBreakdownForBm25()
    {
        var index = CreateIndex(null, With("1", "the archival document retains original data", "docs"));

        var explanation = index.Explain("1", "archival data");

        Assert.NotNull(explanation);
        Assert.Equal("BM25", explanation!.Algorithm);
        Assert.Contains(explanation.Terms, t => t.Term == "archival" && t.Score > 0);
        Assert.Contains(explanation.Terms, t => t.Term == "data" && t.Score > 0);
        Assert.Equal(explanation.TotalScore, explanation.Terms.Sum(t => t.Score), precision: 10);
    }

    [Fact]
    public void ScorerHelpers_SwitchAlgorithm()
    {
        var tfIdf = CreateIndex(o => o.UseTfIdf(), With("1", "alpha beta gamma", "docs"));
        var ql = CreateIndex(o => o.UseQueryLikelihood(), With("1", "alpha beta gamma", "docs"));

        Assert.Equal("TF-IDF", tfIdf.Explain("1", "alpha")!.Algorithm);
        Assert.Equal("QueryLikelihood", ql.Explain("1", "alpha")!.Algorithm);
    }

    [Fact]
    public void UseBoolean_FiltersLikeAnExactAnd()
    {
        var boolean = CreateIndex(o => o.UseBoolean(),
            With("1", "alpha beta", "docs"),
            With("2", "gamma delta", "docs"));

        Assert.Empty(boolean.Search("alpha gamma"));
        Assert.Single(boolean.Search("alpha beta"));
        Assert.Single(boolean.Search("alpha"));
    }

    [Fact]
    public void EnableFuzzy_MatchesCloseTerms()
    {
        var strict = CreateIndex(null, With("1", "the cat sat on the mat", "docs"));
        Assert.Empty(strict.Search("catt"));

        var fuzzy = CreateIndex(o => o.EnableFuzzy = true, With("1", "the cat sat on the mat", "docs"));
        var hit = RequireSingle(fuzzy.Search("catt"));
        Assert.Equal("1", hit.DocumentId);
    }

    [Fact]
    public void EnableFuzzy_LeavesExplicitOperatorsUntouched()
    {
        var fuzzy = CreateIndex(o => o.EnableFuzzy = true,
            With("1", "retrieval systems", "docs"),
            With("2", "retained records", "docs"));

        var hits = fuzzy.Search("retrie*");

        var hit = RequireSingle(hits);
        Assert.Equal("1", hit.DocumentId);
    }

    [Fact]
    public void FuzzyOnlyOutOfVocabulary_KeepsKnownTermsExact_AndStillCorrectsTypos()
    {
        var docs = new[]
        {
            With("1", "keycloak login recovery", "docs"),
            With("2", "logins proxy", "docs"),
        };

        var strict = CreateIndex(o =>
        {
            o.EnableFuzzy = true;
            o.FuzzyOnlyOutOfVocabulary = true;
        }, docs);
        var plain = CreateIndex(o => o.EnableFuzzy = true, docs);

        // "login" is in the vocabulary: with the flag it stays exact, "logins" no longer leaks in.
        Assert.Equal(new[] { "1" }, strict.Search("login").Select(h => h.DocumentId));
        Assert.Equal(new[] { "1", "2" }, plain.Search("login").Select(h => h.DocumentId).OrderBy(x => x));

        // "keycloack" is out of vocabulary: the typo is still corrected to "keycloak".
        Assert.Equal("1", RequireSingle(strict.Search("keycloack")).DocumentId);
    }

    [Fact]
    public void Highlight_ProducesWrappedText()
    {
        var index = CreateIndex(null, With("1", "the quick brown fox", "docs"));

        var hit = RequireSingle(index.Search("quick brown", new LexiSharpQueryOptions(Highlight: true)));

        Assert.Equal("the <em>quick</em> <em>brown</em> fox", hit.HighlightedText);
    }

    [Fact]
    public void Reranker_ReordersBeforeLimit()
    {
        var index = CreateIndex(o => o.Reranker = new ReverseReranker(), With("1", "alpha first", "docs"));

        Assert.Equal("1", RequireSingle(index.Search("alpha")).DocumentId);
    }

    [Fact]
    public void Statistics_ReflectCorpus()
    {
        var index = CreateIndex(null,
            With("1", "one two", "docs"),
            With("2", "three four five", "docs"));

        Assert.Equal(2, index.Count);
        Assert.Equal(5, index.Statistics.TokenCount);
        Assert.Equal(new[] { "1", "2" }, index.DocumentIds);
    }

    private sealed class ReverseReranker : IReranker
    {
        public string Name => "Reverse";

        public IReadOnlyList<SearchResult> Rerank(string query, IReadOnlyList<SearchResult> candidates) =>
            candidates.Reverse().ToList();
    }

    /// <summary>An expander that adds nothing: enough to reach the constructor's refusal.</summary>
    private sealed class NoOpExpander : ITermExpander
    {
        public IReadOnlyCollection<ExpandedTerm> Expand(IReadOnlyList<string> terms) => [];
    }

    /// <summary>A persistent facade whose documents live in a caller-owned dictionary, as a real one would.</summary>
    private static (LexiSharpIndex<SampleDoc> Index, Dictionary<string, SampleDoc> Corpus) CreatePersistent(
        string directory,
        params (string Id, string Body, string Category, string[] Tags)[] docs)
    {
        var corpus = docs.ToDictionary(
            d => d.Id, d => new SampleDoc(d.Id, d.Body, d.Category, d.Tags), StringComparer.Ordinal);

        var index = new LexiSharpIndex<SampleDoc>(o =>
        {
            o.Id = d => d.Id;
            o.Text = d => d.Body;
            o.Directory = directory;
            o.Load = id => corpus.GetValueOrDefault(id);
        });

        index.AddRange(corpus.Values);
        return (index, corpus);
    }

    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), "lexisharp-facade-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void PersistentIndex_CommitSurvivesReopen()
    {
        // The point of a store: the corpus is on disk, and a fresh facade over the same directory
        // serves the same page without being handed the documents again. Only the id-to-document
        // mapping has to come back, which is what Load is for.
        string directory = NewDirectory();

        try
        {
            var (index, corpus) = CreatePersistent(directory,
                With("1", "alpha beta", "docs"),
                With("2", "beta gamma", "docs"));

            Assert.True(index.IsPersistent);

            var before = index.Search("beta").Select(h => h.DocumentId).ToArray();
            index.Commit();
            index.Dispose();

            var reopened = new LexiSharpIndex<SampleDoc>(o =>
            {
                o.Id = d => d.Id;
                o.Text = d => d.Body;
                o.Directory = directory;
                o.Load = id => corpus.GetValueOrDefault(id);
            });

            try
            {
                Assert.Equal(2, reopened.Count);
                Assert.Equal(before, reopened.Search("beta").Select(h => h.DocumentId).ToArray());

                // The hit carries the caller's document, resolved through Load rather than held.
                var hit = RequireSingle(reopened.Search("gamma"));
                Assert.Equal("2", hit.DocumentId);
                Assert.Equal(corpus["2"], hit.Document);
            }
            finally
            {
                reopened.Dispose();
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PersistentIndex_SearchReadsTheStoreTheCommitReturned()
    {
        // Commit replaces the store with a new one, and the facade rebuilds the pipeline around it.
        // A search after the commit must see the committed corpus, not the store it replaced — this
        // is the lifecycle hazard the facade exists to hide.
        string directory = NewDirectory();

        try
        {
            var (index, _) = CreatePersistent(directory, With("1", "alpha beta", "docs"));

            var engineBefore = index.Engine;
            index.Commit();

            Assert.NotSame(engineBefore, index.Engine);
            Assert.Equal("1", RequireSingle(index.Search("alpha")).DocumentId);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PersistentIndex_MergeReclaimsTombstonesWithoutWriting()
    {
        string directory = NewDirectory();

        try
        {
            var (index, corpus) = CreatePersistent(directory,
                With("1", "alpha beta", "docs"),
                With("2", "beta gamma", "docs"));

            index.Remove("1");
            Assert.Equal(1, index.Count);

            index.Merge();

            Assert.Equal(1, index.Count);
            Assert.Equal("2", RequireSingle(index.Search("gamma")).DocumentId);
            Assert.Equal("2", corpus["2"].Id);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PersistentIndex_RefusesATermExpander()
    {
        string directory = NewDirectory();

        try
        {
            var error = Assert.Throws<ArgumentException>(() =>
                new LexiSharpIndex<SampleDoc>(o =>
                {
                    o.Id = d => d.Id;
                    o.Text = d => d.Body;
                    o.Directory = directory;
                    o.TermExpander = new NoOpExpander();
                }));

            Assert.Contains("term expander", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PersistentIndex_AnUnresolvableHitNamesTheOption()
    {
        // A store carries documents this session never added; without Load there is no caller document
        // to put in the hit, and a silent null would be a hit the caller cannot use.
        string directory = NewDirectory();

        try
        {
            var (writer, _) = CreatePersistent(directory, With("1", "alpha beta", "docs"));
            writer.Commit();
            writer.Dispose();

            var reader = new LexiSharpIndex<SampleDoc>(o =>
            {
                o.Id = d => d.Id;
                o.Text = d => d.Body;
                o.Directory = directory;
            });

            try
            {
                var error = Assert.Throws<InvalidOperationException>(() => reader.Search("alpha"));
                Assert.Contains("Load", error.Message, StringComparison.Ordinal);
            }
            finally
            {
                reader.Dispose();
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PersistentIndex_WithSearchDocumentResolvesWithoutAResolver()
    {
        // SearchDocument is the one type the store can hand back itself, so a store of them needs no
        // caller-side mapping.
        string directory = NewDirectory();

        try
        {
            var writer = new LexiSharpIndex<SearchDocument>(o => o.Directory = directory);
            writer.Add(new SearchDocument("1", "alpha beta"));
            writer.Commit();
            writer.Dispose();

            var reader = new LexiSharpIndex<SearchDocument>(o => o.Directory = directory);

            try
            {
                var hit = RequireSingle(reader.Search("alpha"));
                Assert.Equal("1", hit.DocumentId);
                Assert.Equal("alpha beta", hit.Document.Text);
            }
            finally
            {
                reader.Dispose();
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}