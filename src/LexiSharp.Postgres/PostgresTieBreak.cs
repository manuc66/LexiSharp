using LexiSharp.Core;
using LexiSharp.Ranking;

namespace LexiSharp.Postgres;

/// <summary>
/// The tie-break gate every SQL engine runs before it opens a connection.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TieBreak.DocumentId"/> is what these backends implement: the statement ends with
/// <c>id COLLATE "C"</c>, which is the ordinal order the in-memory engines use by default.
/// <see cref="TieBreak.InsertionOrder"/> reads the document's ordinal — the position it holds in the
/// index's ordinal space — and a SQL table has no such position for the engine to read. Ordering by
/// the heap's physical order would approximate it and then stop being true after an update, a
/// <c>VACUUM FULL</c> or a different plan, so the option is refused where it cannot be honoured
/// rather than answered with a different order under its name.
/// </para>
/// <para>
/// The check runs before the connection, so a caller learns about the combination without a server.
/// </para>
/// </remarks>
internal static class PostgresTieBreak
{
    /// <summary>Refuses <see cref="TieBreak.InsertionOrder"/> on a SQL backend.</summary>
    /// <param name="options">The search options the caller passed.</param>
    /// <exception cref="NotSupportedException">The options ask for insertion order.</exception>
    public static void EnsureSupported(SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.TieBreak == TieBreak.InsertionOrder)
        {
            throw new NotSupportedException(
                "TieBreak.InsertionOrder orders equal scores by the document's ordinal, which is a "
                + "position in the in-memory index's ordinal space. A SQL table has no such position "
                + "for the engine to read, so this backend cannot honour it. Use TieBreak.DocumentId "
                + "(the default), which every SQL engine implements as `id COLLATE \"C\"`.");
        }
    }
}
