namespace LexiSharp.Sources;

/// <summary>Knobs for <see cref="TextFileLoader"/>.</summary>
public sealed record TextFileLoadOptions
{
    /// <summary>Whether <see cref="TextFileLoader.ScanDirectory"/> descends into subdirectories. Default: <c>true</c>.</summary>
    public bool Recursive { get; init; } = true;

    /// <summary>Extensions treated as text (ordinal, case-insensitive). Default: <c>.txt</c>, <c>.md</c>, <c>.markdown</c>.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = new[] { ".txt", ".md", ".markdown" };
}

/// <summary>
/// Loads plain text files from a directory, whole file content becoming the indexed text.
/// </summary>
/// <remarks>
/// Each document id is the file's path relative to the scanned root, with forward-slash
/// separators; the <c>title</c> field is the file name and <c>source</c> the relative path.
/// Hidden files and files under a hidden directory are skipped.
/// </remarks>
public static class TextFileLoader
{
    /// <summary>Reads every matching text file under <paramref name="root"/>.</summary>
    public static IReadOnlyList<LoadedDocument> ScanDirectory(string root, TextFileLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(root);

        var rootPath = Path.GetFullPath(root);
        var loaderOptions = options ?? new TextFileLoadOptions();
        var documents = new List<LoadedDocument>();

        foreach (var file in FileSystemScan.Enumerate(rootPath, loaderOptions.Extensions, loaderOptions.Recursive))
        {
            string relativeId = FileSystemScan.RelativeId(rootPath, file);
            var fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["title"] = Path.GetFileName(file),
                ["source"] = relativeId,
            };

            documents.Add(new LoadedDocument(relativeId, File.ReadAllText(file), fields));
        }

        return documents;
    }
}