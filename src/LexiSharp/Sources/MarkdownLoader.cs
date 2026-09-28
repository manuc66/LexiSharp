
// CA1859 ("use a concrete type instead of the interface") is suppressed on the lines
// below. The interface is the published return type: a List<T> or an array in its place
// would hand callers a mutable collection through a contract that says they cannot have
// one, and what it saves is a single interface dispatch per call, which no measurement in
// docs/benchmarks.md attributes time to.

namespace LexiSharp.Sources;

/// <summary>Knobs for <see cref="MarkdownLoader"/>.</summary>
public sealed record MarkdownLoadOptions
{
    /// <summary>Whether <see cref="MarkdownLoader.LoadDirectory"/> descends into subdirectories. Default: <c>true</c>.</summary>
    public bool Recursive { get; init; } = true;

    /// <summary>Extensions treated as markdown (ordinal, case-insensitive). Default: <c>.md</c>, <c>.markdown</c>, <c>.mdx</c>.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = new[] { ".md", ".markdown", ".mdx" };

    /// <summary>Whether every YAML front-matter entry becomes a document field. Default: <c>true</c>.</summary>
    public bool KeepFrontmatterFields { get; init; } = true;

    /// <summary>Whether the first <c># Heading</c> fills the <c>title</c> field when the front matter has none. Default: <c>true</c>.</summary>
    public bool FirstHeadingAsTitle { get; init; } = true;

    /// <summary>
    /// Front-matter keys to expose additionally as <see cref="Core.SearchDocument.TextFields"/>,
    /// so a field-weighted scorer (<c>Bm25FScorer</c>) can weigh them. Matched against the resolved
    /// front matter, so <c>title</c> also picks up a heading-synthesized title. Default: none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaulting to none is deliberate. Front matter is mostly metadata — a date, a status, an
    /// author id — and promoting all of it to indexed text would make those values match queries,
    /// which is rarely what anyone wants. Name the keys that are genuinely part of the document's
    /// prose, typically <c>["title"]</c>.
    /// </para>
    /// <para>
    /// A promoted key is <b>in addition to</b> the document field it already was: the title stays
    /// filterable and facetable as a field, and additionally becomes searchable text. Its tokens
    /// also join the flat view, so a plain BM25 query finds a term that only appears in the title.
    /// </para>
    /// </remarks>
    public IReadOnlyCollection<string> TextFieldNames { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Loads markdown documents: the optional YAML front matter (<c>---</c>-delimited block at the
/// top of the file) is parsed into structured fields, and the rest of the file is the indexed
/// text.
/// </summary>
/// <remarks>
/// Front matter is a simple <c>key: value</c> format. <c>title</c>, <c>category</c> and
/// <c>tags</c> are recognized (the category also becomes the loaded document's category;
/// <c>tags</c> accepts <c>[a, b]</c> or <c>a, b</c> lists); any other key becomes a document
/// field unless <see cref="MarkdownLoadOptions.KeepFrontmatterFields"/> is false. Multiline or
/// nested YAML structures are not supported — lines without a colon are ignored.
/// </remarks>
public static class MarkdownLoader
{
    private const string NoFileId = "document";

    /// <summary>
    /// Parses a markdown string with no file context; the returned document id is
    /// <c>"document"</c>.
    /// </summary>
    public static LoadedDocument Parse(string markdown) => Parse(markdown, new MarkdownLoadOptions());

    /// <summary>
    /// Parses a markdown string with no file context, honouring
    /// <paramref name="options"/> (notably <see cref="MarkdownLoadOptions.TextFieldNames"/>); the
    /// returned document id is <c>"document"</c>.
    /// </summary>
    public static LoadedDocument Parse(string markdown, MarkdownLoadOptions options)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(options);

        return ParseCore(markdown, NoFileId, options);
    }

    /// <summary>Reads and parses one markdown file; the document id is the file's full path.</summary>
    public static LoadedDocument LoadFile(string path, MarkdownLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        return ParseCore(
            File.ReadAllText(path),
            Path.GetFullPath(path),
            options ?? new MarkdownLoadOptions());
    }

    /// <summary>
    /// Reads every markdown file under <paramref name="path"/>; each document id is the file's
    /// path relative to the scanned root, with forward-slash separators.
    /// </summary>
    public static IReadOnlyList<LoadedDocument> LoadDirectory(string path, MarkdownLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        var root = Path.GetFullPath(path);
        var loaderOptions = options ?? new MarkdownLoadOptions();
        var documents = new List<LoadedDocument>();

        foreach (var file in FileSystemScan.Enumerate(root, loaderOptions.Extensions, loaderOptions.Recursive))
            documents.Add(ParseCore(File.ReadAllText(file), FileSystemScan.RelativeId(root, file), loaderOptions));

        return documents;
    }

    private static LoadedDocument ParseCore(string markdown, string id, MarkdownLoadOptions options)
    {
        var (entries, category, body) = ParseFrontMatter(markdown);

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        if (options.FirstHeadingAsTitle && !entries.ContainsKey("title"))
        {
            var heading = FirstHeading(body);

            if (heading is not null)
                fields["title"] = heading;
        }

        foreach (var (key, value) in entries)
            fields[key] = value;

        fields["source"] = id;

        return new LoadedDocument(id, body, fields, category, PromoteTextFields(fields, options));
    }

    /// <summary>
    /// Picks the requested keys out of the resolved front matter, skipping the ones this document
    /// does not carry and the ones that hold nothing. Returns null rather than an empty map so a
    /// document with no promoted field is indistinguishable from one that was never asked for.
    /// </summary>
    private static Dictionary<string, string>? PromoteTextFields(
        IReadOnlyDictionary<string, string> fields, // NOSONAR:CA1859
        MarkdownLoadOptions options)
    {
        if (options.TextFieldNames.Count == 0)
            return null;

        Dictionary<string, string>? promoted = null;

        foreach (string name in options.TextFieldNames)
        {
            if (!fields.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value))
                continue;

            promoted ??= new Dictionary<string, string>(StringComparer.Ordinal);
            promoted[name] = value;
        }

        return promoted;
    }

    private static (IReadOnlyDictionary<string, string> Fields, string? Category, string Body) ParseFrontMatter(
        string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string? category = null;
        string body = text;

        if (!StartsWithDelimiter(text))
            return (fields, category, body);

        // The opening "---" is at the very start; collect the lines until a closing "---".
        int cursor = 3;

        if (cursor < text.Length && text[cursor] == '\r')
            cursor++;

        if (cursor < text.Length && text[cursor] == '\n')
            cursor++;

        var entries = new List<KeyValuePair<string, string>>();
        int bodyStart = -1;

        while (cursor <= text.Length)
        {
            int newline = text.IndexOf('\n', cursor);
            int lineEnd = newline < 0 ? text.Length : newline;
            string line = text[cursor..lineEnd].TrimEnd('\r');

            if (line == "---")
            {
                bodyStart = newline < 0 ? text.Length : newline + 1;
                break;
            }

            AddEntry(entries, line);

            if (newline < 0)
                break;

            cursor = newline + 1;
        }

        foreach (var (key, value) in entries)
        {
            switch (key)
            {
                case "title":
                    fields["title"] = value;
                    break;
                case "category":
                    category = value;
                    fields["category"] = value;
                    break;
                case "tags":
                    fields["tags"] = value;
                    break;
                default:
                    fields[key] = value;
                    break;
            }
        }

        if (bodyStart >= 0)
            body = text[bodyStart..];

        return (fields, category, body);
    }

    private static bool StartsWithDelimiter(string text) =>
        text.AsSpan().StartsWith("---", StringComparison.Ordinal);

    /// <summary>Parses one <c>key: value</c> front-matter line into an entry, when it has one.</summary>
    private static void AddEntry(List<KeyValuePair<string, string>> entries, string line)
    {
        int colon = line.IndexOf(':');

        if (colon <= 0)
            return;

        string key = line[..colon].Trim();

        if (key.Length == 0)
            return;

        entries.Add(new KeyValuePair<string, string>(key, NormalizeValue(line[(colon + 1)..].Trim())));
    }

    /// <summary>Strips surrounding quotes and flattens a <c>[a, b]</c> list to <c>"a, b"</c>.</summary>
    private static string NormalizeValue(string value)
    {
        if (value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'')
            value = value[1..^1].Trim();

        if (value.Length >= 2 && value[0] == '[' && value[^1] == ']')
        {
            var items = value[1..^1]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            value = string.Join(", ", items);
        }

        return value;
    }

    /// <summary>The first <c># Heading</c> of the body, or <c>null</c> when there is none.</summary>
    private static string? FirstHeading(string body)
    {
        foreach (var line in ReadLines(body))
        {
            if (line.Length < 2 || line[0] != '#')
                continue;

            int content = 1;

            while (content < line.Length && line[content] == '#')
                content++;

            if (content < line.Length && line[content] != ' ')
                continue;

            var heading = line[content..].Trim();

            if (heading.Length > 0)
                return heading;
        }

        return null;
    }

    /// <summary>Yields the lines of a text, without the trailing newline characters.</summary>
    private static IEnumerable<string> ReadLines(string text)
    {
        int cursor = 0;

        while (cursor <= text.Length)
        {
            int newline = text.IndexOf('\n', cursor);
            int lineEnd = newline < 0 ? text.Length : newline;

            yield return text[cursor..lineEnd].TrimEnd('\r');

            if (newline < 0)
                yield break;

            cursor = newline + 1;
        }
    }
}