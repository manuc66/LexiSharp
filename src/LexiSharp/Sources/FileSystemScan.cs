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
    /// <remarks>
    /// The order is ordinal-sorted, and it has to be. <see cref="Directory.EnumerateFiles(string, string,
    /// SearchOption)"/> yields entries in raw <c>readdir</c> order, which is a property of the
    /// filesystem rather than of the tree: an ext4 directory with <c>dir_index</c> returns hashed
    /// names, and the same checkout enumerates differently on a developer machine and on a CI
    /// runner. Document insertion order is observable, because ranking breaks score ties on
    /// enumeration order, so an unsorted walk made result order environment-dependent.
    /// </remarks>
    public static IEnumerable<string> Enumerate(string root, IReadOnlyList<string> extensions, bool recursive)
    {
        var wanted = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        // Collected before yielding: a total order cannot be produced by streaming an unordered
        // sequence, and the alternative -- letting callers sort -- pushes the requirement onto
        // every one of them.
        var matches = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*", searchOption))
        {
            if (!wanted.Contains(Path.GetExtension(file)))
                continue;

            if (!IsVisible(file, root))
                continue;

            matches.Add(file);
        }

        matches.Sort(StringComparer.Ordinal);

        return matches;
    }

    /// <summary>Whether no path component from the root onward starts with a dot.</summary>
    private static bool IsVisible(string file, string root)
    {
        var relative = Path.GetRelativePath(root, file);

        // Spelled out as a collection so the call cannot silently change meaning. Written in
        // expanded-params form -- Split(a, b) -- it binds to a two-separator split only because
        // neither Split(char, int) nor Split(char, StringSplitOptions) accepts a second char, which
        // is a coincidence of the overload set rather than something the call states. The result is
        // unchanged: on Unix the two constants are the same character, so nothing here is
        // observable on the host these tests run on -- it is the Windows path that has two.
        foreach (var part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]))
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