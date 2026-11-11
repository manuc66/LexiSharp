namespace LexiSharp;

/// <summary>
/// Guards file paths against traversal sequences such as <c>..</c>, which would let a caller
/// escape an intended base directory.
/// </summary>
/// <remarks>
/// The check is deliberately conservative: any path containing a <c>..</c> segment (or the
/// two-character sequence <c>..</c> at all) is rejected. Legitimate file names do not need it,
/// and rejecting it removes the ambiguity a security scanner cannot resolve on its own.
/// </remarks>
public static class PathSafety
{
    /// <summary>Returns <c>true</c> when <paramref name="path"/> contains no traversal sequence.</summary>
    public static bool IsSafe(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return true;

        if (path.Contains("..", StringComparison.Ordinal))
            return false;

        foreach (var segment in path.Split(new[] { '/', '\\' }))
        {
            if (segment == "..")
                return false;
        }

        return true;
    }

    /// <summary>Throws <see cref="ArgumentException"/> when <paramref name="path"/> is unsafe.</summary>
    public static void ThrowIfUnsafe(string? path, string paramName = "path")
    {
        if (IsSafe(path))
            return;

        throw new ArgumentException("Path must not contain '..'.", paramName);
    }
}