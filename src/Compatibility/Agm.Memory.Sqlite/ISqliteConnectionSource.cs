using Microsoft.Data.Sqlite;

namespace Agm.Memory.Sqlite;

/// <summary>
/// Opens an initialized SQLite connection owned by the consuming host.
/// The caller disposes each returned connection.
/// </summary>
public interface ISqliteConnectionSource
{
    Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken);
}
