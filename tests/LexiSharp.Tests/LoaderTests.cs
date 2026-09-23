using LexiSharp.Sources;
using Xunit;

namespace LexiSharp.Tests;

public class LoaderTests
{
    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = Directory.CreateTempSubdirectory("lexisharp-sources-").FullName;
        }

        public string Path { get; }

        public string Write(string relativePath, string content)
        {
            string fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relativePath));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
            return fullPath;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private const string FrontMatter =
        """
        ---
        title: My Title
        category: docs
        tags: [alpha, beta]
        custom: hello "quoted"
        ---
        # Body heading
        The body text.
        """;

    [Fact]
    public void MarkdownLoader_Parse_SplitsFrontMatterFromBody()
    {
        var document = MarkdownLoader.Parse(FrontMatter);

        Assert.Equal("document", document.Id);
        Assert.Equal("docs", document.Category);
        Assert.Equal("# Body heading\nThe body text.", document.Text);
        Assert.Equal("My Title", document.Fields!["title"]);
        Assert.Equal("docs", document.Fields["category"]);
        Assert.Equal("alpha, beta", document.Fields["tags"]);
        Assert.Equal("hello \"quoted\"", document.Fields["custom"]);
    }

    [Fact]
    public void MarkdownLoader_Parse_FirstHeadingFillsTitleWhenNoFrontMatterTitle()
    {
        const string markdown = "# Architecture notes\n\nGuidelines for the distributed system.";

        var document = MarkdownLoader.Parse(markdown);

        Assert.Equal("Architecture notes", document.Fields!["title"]);
        Assert.Equal(markdown, document.Text);
    }

    [Fact]
    public void MarkdownLoader_Parse_NoFrontMatter_KeepsWholeTextAsBody()
    {
        var document = MarkdownLoader.Parse("just a plain note");

        Assert.Equal("just a plain note", document.Text);
        Assert.Null(document.Category);
        Assert.Contains("document", document.Fields!["source"]);
    }

    [Fact]
    public void MarkdownLoader_LoadFile_UsesFullPathAsId()
    {
        using var dir = new TempDir();
        string path = dir.Write("note.md", "---\ntitle: Note\n---\nbody here");

        var document = MarkdownLoader.LoadFile(path);

        Assert.Equal(System.IO.Path.GetFullPath(path), document.Id);
        Assert.Equal("Note", document.Fields!["title"]);
    }

    [Fact]
    public void MarkdownLoader_LoadDirectory_RelativeIds_RecursiveAndFiltered()
    {
        using var dir = new TempDir();
        dir.Write("a.md", "---\ntitle: A\n---\nalpha body");
        dir.Write("notes/b.markdown", "# B\n\nbeta body");
        dir.Write("notes/c.txt", "not markdown");
        dir.Write("notes/.hidden/d.md", "hidden body");

        var documents = MarkdownLoader.LoadDirectory(dir.Path);

        Assert.Equal(new[] { "a.md", "notes/b.markdown" }, documents.Select(d => d.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal("B", documents.Single(d => d.Id == "notes/b.markdown").Fields!["title"]);
    }

    [Fact]
    public void MarkdownLoader_LoadDirectory_NonRecursive_TopLevelOnly()
    {
        using var dir = new TempDir();
        dir.Write("a.md", "top level");
        dir.Write("sub/b.md", "nested");

        var documents = MarkdownLoader.LoadDirectory(dir.Path, new MarkdownLoadOptions { Recursive = false });

        Assert.Equal(new[] { "a.md" }, documents.Select(d => d.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void TextFileLoader_ScanDirectory_LoadsWholeFileWithMetadata()
    {
        using var dir = new TempDir();
        dir.Write("readme.md", "# Readme\ncontent");
        dir.Write("data.txt", "raw text");

        var documents = TextFileLoader.ScanDirectory(dir.Path);

        Assert.Equal(2, documents.Count);
        var data = documents.Single(d => d.Id == "data.txt");
        Assert.Equal("raw text", data.Text);
        Assert.Equal("data.txt", data.Fields!["title"]);
        Assert.Equal("data.txt", data.Fields["source"]);
    }

    [Fact]
    public void JsonDocumentsLoader_Parse_ReadsScalarsAndFields()
    {
        const string json =
            """
            [
              { "id": "1", "text": "hello world", "lang": "en", "tags": ["a", "b"], "flag": true },
              { "id": "2", "text": "second" }
            ]
            """;

        var documents = JsonDocumentsLoader.Parse(json);

        Assert.Equal(2, documents.Count);
        Assert.Equal("1", documents[0].Id);
        Assert.Equal("hello world", documents[0].Text);
        Assert.Equal("en", documents[0].Fields!["lang"]);
        Assert.Equal("a, b", documents[0].Fields["tags"]);
        Assert.Equal("true", documents[0].Fields["flag"]);
        Assert.Equal("2", documents[1].Id);
    }

    [Fact]
    public void JsonDocumentsLoader_Parse_RespectsCustomPropertyNames()
    {
        const string json =
            """
            [{ "docid": "a", "content": "some text", "bucket": "hot", "extra": "x" }]
            """;

        var options = new JsonDocumentLoadOptions
        {
            IdProperty = "docid",
            TextProperty = "content",
            CategoryProperty = "bucket",
        };

        var document = JsonDocumentsLoader.Parse(json, options).Single();

        Assert.Equal("a", document.Id);
        Assert.Equal("some text", document.Text);
        Assert.Equal("hot", document.Category);
        Assert.Equal("x", document.Fields!["extra"]);
        Assert.DoesNotContain("bucket", document.Fields.Keys);
    }

    [Fact]
    public void JsonDocumentsLoader_Parse_NoAdditionalFields()
    {
        const string json =
            """
            [{ "id": "a", "text": "some text", "lang": "en" }]
            """;

        var document = JsonDocumentsLoader.Parse(json, new JsonDocumentLoadOptions
        {
            AdditionalPropertiesAsFields = false,
        }).Single();

        Assert.Empty(document.Fields);
    }

    [Fact]
    public void JsonDocumentsLoader_Parse_ThrowsOnMissingText()
    {
        const string json = """[{ "id": "a" }]""";

        var exception = Assert.Throws<ArgumentException>(() => JsonDocumentsLoader.Parse(json));
        Assert.Contains("'text'", exception.Message);
    }

    [Fact]
    public void JsonDocumentsLoader_Parse_ThrowsWhenRootIsNotAnArray()
    {
        const string json = """{ "id": "a", "text": "b" }""";

        Assert.Throws<ArgumentException>(() => JsonDocumentsLoader.Parse(json));
    }

    [Fact]
    public void JsonDocumentsLoader_LoadFile_ReadsFromDisk()
    {
        using var dir = new TempDir();
        string path = dir.Write("docs.json", """[{ "id": "1", "text": "loaded from disk" }]""");

        var document = JsonDocumentsLoader.LoadFile(path).Single();

        Assert.Equal("loaded from disk", document.Text);
    }

    [Fact]
    public void LoadedDocument_IndexesThroughTheFacade()
    {
        var index = new LexiSharpIndex<LoadedDocument>(o =>
        {
            o.Id = d => d.Id;
            o.Text = d => d.Text;
            o.Fields = d => d.Fields;
            o.Category = d => d.Category;
        });

        index.Add(MarkdownLoader.Parse("---\ntitle: Distributed systems\ncategory: architecture\n---\nthe router orchestrates the services"));

        var hit = Assert.Single(index.Search("orchestrates"));

        Assert.Equal("architecture", hit.Document.Category);
        Assert.Equal("Distributed systems", hit.Document.Fields!["title"]);
    }
}