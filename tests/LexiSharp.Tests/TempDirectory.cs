using System;
using System.IO;

namespace LexiSharp.Tests;

/// <summary>
/// A temporary directory that deletes itself (and everything under it) when disposed.
/// Use this instead of <see cref="Directory.CreateTempSubdirectory"/> / <see cref="Path.GetTempPath"/>
/// + manual cleanup, so an interrupted test cannot leave a <c>lexisharp-*</c> directory behind
/// and starve the device of free space (see BLINDSPOTS: 1 616 such directories = 1.1 GiB).
/// </summary>
/// <remarks>
/// <para>
/// Always wrap in a <c>using</c> or <c>await using</c>; never store the path past disposal.
/// </para>
/// <para>
/// The cleanup is best-effort: if a file is locked (e.g. a memory-mapped segment still open),
/// <see cref="Dispose"/> swallows the error so a teardown failure cannot mask the test result,
/// but the directory may survive. The CI guard step removes any leftovers before the next run.
/// </para>
/// </remarks>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    private TempDirectory(string path) => Path = path;

    public static TempDirectory Create(string prefix = "lexisharp")
    {
        string path = Directory.CreateTempSubdirectory(prefix + "-").FullName;
        return new TempDirectory(path);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup: a locked file (open handle, mmap) must not turn a passing
            // test into a failing one. The CI guard step removes leftovers before the next run.
        }
    }
}