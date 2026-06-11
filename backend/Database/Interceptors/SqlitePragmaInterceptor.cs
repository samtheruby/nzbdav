using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NzbWebDAV.Database.Interceptors;

/// <summary>
/// Applies the per-connection SQLite pragmas the app relies on, on every open:
///   * journal_mode=WAL    — readers don't block the writer (and vice versa),
///                           so the concurrent background services / parallel
///                           health checks don't trip "database is locked".
///   * busy_timeout=30000  — wait up to 30s for the write lock instead of
///                           failing immediately under contention.
///   * synchronous=NORMAL  — safe and faster under WAL.
///   * foreign_keys=ON     — enforce referential integrity.
/// </summary>
public class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragmas =
        "PRAGMA journal_mode=WAL;" +
        "PRAGMA busy_timeout=30000;" +
        "PRAGMA synchronous=NORMAL;" +
        "PRAGMA foreign_keys=ON;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync
    (
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
