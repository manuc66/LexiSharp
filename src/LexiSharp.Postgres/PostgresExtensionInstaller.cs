using Npgsql;

namespace LexiSharp.Postgres;

/// <summary>
/// Serializes <c>CREATE EXTENSION</c> across the process: the statement is not
/// concurrency-safe even with <c>IF NOT EXISTS</c> — concurrent installs race on
/// <c>pg_extension_name_index</c> and one of them fails with SQLSTATE 23505.
/// Engines bootstrap in parallel under xUnit, and real applications may start
/// several engines at once, so every extension install goes through here.
/// </summary>
internal static class PostgresExtensionInstaller
{
    private static readonly SemaphoreSlim Lock = new(1, 1);

    public static async Task InstallAsync(
        NpgsqlConnection connection,
        string statements,
        CancellationToken cancellationToken)
    {
        await Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statements;
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                // Lost a CREATE EXTENSION race against another process or host:
                // the extension is present, which is all IF NOT EXISTS promises.
            }
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>
    /// Synchronous <see cref="InstallAsync"/>, for a caller whose own API is synchronous.
    /// </summary>
    /// <remarks>
    /// It shares the same lock as the async overload, so a sync and an async install still cannot
    /// race. The wait is on the caller's thread rather than on a thread-pool continuation: a
    /// synchronous constructor must not block a pool thread waiting for work that the pool owes it.
    /// </remarks>
    public static void Install(NpgsqlConnection connection, string statements)
    {
        Lock.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = statements;
            try
            {
                command.ExecuteNonQuery();
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                // Lost a CREATE EXTENSION race against another process or host:
                // the extension is present, which is all IF NOT EXISTS promises.
            }
        }
        finally
        {
            Lock.Release();
        }
    }
}
