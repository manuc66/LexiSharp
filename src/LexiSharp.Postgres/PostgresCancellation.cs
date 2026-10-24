using Npgsql;

namespace LexiSharp.Postgres;

/// <summary>
/// Makes a blocking <see cref="NpgsqlCommand"/> observable to a <see cref="CancellationToken"/>.
/// </summary>
/// <remarks>
/// <para>
/// The PostgreSQL engines execute synchronously, and Npgsql's blocking API takes no token — that is
/// why <c>PostgresTextSearchEngine</c>, <c>PostgresFuzzySearchEngine</c> and
/// <c>ParadeDBTextSearchEngine</c> accepted the parameter on <c>Search</c> and could not consult it.
/// Cancellation reaches the server through <see cref="NpgsqlCommand.Cancel"/>, which Npgsql expects
/// to be called from another thread: the registration fires on the thread that cancels, not on the
/// one blocked in the reader.
/// </para>
/// <para>
/// A registration created on a token that is already cancelled fires immediately, so the callers
/// check <see cref="CancellationToken.ThrowIfCancellationRequested"/> before registering. Calling
/// <c>Cancel</c> on a command that is not executing is a no-op, measured on this host against
/// <c>pgvector/pgvector:pg16</c>: no exception, and the command still runs afterwards.
/// </para>
/// <para>
/// The registration is disposed with the command's scope, so a token cancelled after the search has
/// returned calls nothing.
/// </para>
/// </remarks>
internal static class PostgresCancellation
{
    /// <summary>
    /// Observes <paramref name="cancellationToken"/> while <paramref name="command"/> executes.
    /// </summary>
    /// <param name="command">The command whose execution should be cancellable.</param>
    /// <param name="cancellationToken">The token the caller passed to the search.</param>
    /// <returns>A registration the caller disposes when the command's execution is over.</returns>
    public static CancellationTokenRegistration Register(
        NpgsqlCommand command,
        CancellationToken cancellationToken) =>
        cancellationToken.Register(static state => ((NpgsqlCommand)state!).Cancel(), command);
}
