using Microsoft.Data.Sqlite;

namespace Agm.Memory.Sqlite;

/// <summary>A standalone connection source for consumers that own a SQLite database.</summary>
public sealed class SqliteConnectionStringSource : ISqliteConnectionSource
{
    private readonly string connectionString;

    public SqliteConnectionStringSource(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        this.connectionString = connectionString;
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
