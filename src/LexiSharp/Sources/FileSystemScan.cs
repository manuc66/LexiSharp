namespace LexiSharp.Sources;

/// <summary>
/// Shared helpers to enumerate files for the text loaders: extension filtering, hidden-path
/// skipping and forward-slash normalized relative ids.
/// </summary>
internal static class FileSystemScan
{
    /// <summary>
    /// Enumerates the files under <paramref name="root"/> whose extension is listed in
    /// <paramref name="extensions"/> (ordinal, case-insensitive), in deterministic order.
    /// Hidden files and files under a hidden directory are skipped.
    /// </summary>
    public static IEnumerable<string> Enumerate(string root, IReadOnlyList<string> extensions, bool recursive)
    {
        var wanted = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        foreach (var file in Directory.EnumerateFiles(root, "*", searchOption))
        {
            if (!wanted.Contains(Path.GetExtension(file)))
                continue;

            if (!IsVisible(file, root))
                continue;

            yield return file;
        }
    }

    /// <summary>Whether no path component from the root onward starts with a dot.</summary>
    private static bool IsVisible(string file, string root)
    {
        var relative = Path.GetRelativePath(root, file);

        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length > 0 && part[0] == '.')
                return false;
        }

        return true;
    }

    /// <summary>The file path relative to <paramref name="root"/>, with forward-slash separators.</summary>
    public static string RelativeId(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);

        if (Path.DirectorySeparatorChar == '/')
            return relative;

        return relative.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }
}