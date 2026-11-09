using System.Text;
using System.Globalization;
using AgMemory.Contracts;
using Microsoft.Data.Sqlite;

namespace AgMemory.Storage.LanceDb;

/// <summary>Rebuildable exact-scope posting index. Canonical table version and tokenizer version fence every cached generation.</summary>
internal sealed partial class RecallPostingIndex : IDisposable
{
    private const string TokenizerVersion = "whole-token-v1";
    private readonly SqliteConnection _connection;
    private SqliteTransaction? _build;
    private int _rows;
    private readonly string _scopeHash;

    /// <summary>Opens a private bounded cache; a corrupt derived cache can be removed by an operator without changing canonical memory.</summary>
    public RecallPostingIndex(string root, string scopeHash)
    {
        _scopeHash = scopeHash;
        var directory = Path.Combine(root, ".agmemory-recall-index");
        var path = Path.Combine(directory, scopeHash + ".sqlite");
        foreach (var candidate in new[] { root, directory, path, path + "-journal", path + "-wal", path + "-shm" })
            if (new FileInfo(candidate).LinkTarget is not null || new DirectoryInfo(candidate).LinkTarget is not null)
                throw new IOException("Recall index paths cannot be symbolic links.");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (!File.Exists(path))
        {
            if (Directory.EnumerateFiles(directory, "*.sqlite").Take(16).Count() >= 16)
                throw new IOException("Recall index profile capacity reached.");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var unused = new FileStream(path, options);
        }
        _connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            _connection.Open();
            Execute("""
                PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA max_page_count=65536; PRAGMA cache_size=-8192; PRAGMA temp_store=FILE;
                CREATE TABLE IF NOT EXISTS generation(version TEXT NOT NULL,tokenizer TEXT NOT NULL,scope TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS documents(id TEXT PRIMARY KEY,type TEXT NOT NULL,content TEXT NOT NULL,importance REAL NOT NULL,
                  confidence REAL NOT NULL,updated TEXT NOT NULL,dedup TEXT NOT NULL,status TEXT NOT NULL,expires TEXT,length INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS eligible ON documents(status,expires,type);
                CREATE TABLE IF NOT EXISTS postings(term TEXT NOT NULL,id TEXT NOT NULL,frequency INTEGER NOT NULL,ordinal INTEGER NOT NULL,PRIMARY KEY(term,id)) WITHOUT ROWID;
                """);
        }
        catch { _connection.Dispose(); throw; }
    }

    /// <summary>Starts a private rebuild only when the exact native table generation changed; rollback leaves the prior index complete.</summary>
    public bool BeginRebuild(string version)
    {
        using var command = Command("SELECT version,tokenizer,scope FROM generation");
        using var reader = command.ExecuteReader();
        if (reader.Read() && reader.GetString(0) == version && reader.GetString(1) == TokenizerVersion && reader.GetString(2) == _scopeHash) return false;
        reader.Close();
        _build = _connection.BeginTransaction();
        Execute("DELETE FROM generation; DELETE FROM postings; DELETE FROM documents;");
        _rows = 0;
        return true;
    }

    /// <summary>Indexes one streamed document and its whole-token frequencies; no corpus-sized text list is retained.</summary>
    public void Add(string id, string type, string content, double importance, double confidence, string updated,
        string dedup, string status, string? expires, string delimiters)
    {
        if (++_rows > 100_000) throw new IOException("Recall index row capacity reached; use the scan fallback.");
        // A cache must not turn malformed provider metadata into apparently valid recall results.
        if (!Enum.TryParse<MemoryRecordType>(type, out var recordType) || !Enum.IsDefined(recordType) ||
            !double.IsFinite(importance) || importance is < 0 or > 1 || !double.IsFinite(confidence) || confidence is < 0 or > 1 ||
            !DateTimeOffset.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updatedAt) || updatedAt.Offset != TimeSpan.Zero ||
            expires is not null && (!DateTimeOffset.TryParse(expires, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiry) || expiry.Offset != TimeSpan.Zero))
            throw new InvalidDataException("Invalid recall index projection metadata.");
        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
        var length = 0;
        foreach (var range in content.AsSpan().SplitAny(delimiters))
        {
            var token = content.AsSpan()[range].Trim();
            if (token.IsEmpty) continue;
            length++;
            var term = token.ToString().ToUpperInvariant();
            frequencies[term] = frequencies.GetValueOrDefault(term) + 1;
        }
        using (var document = Command("INSERT INTO documents VALUES($id,$type,$content,$importance,$confidence,$updated,$dedup,$status,$expires,$length)"))
        {
            Bind(document, "$id", id); Bind(document, "$type", type); Bind(document, "$content", content);
            Bind(document, "$importance", importance); Bind(document, "$confidence", confidence); Bind(document, "$updated", updated);
            Bind(document, "$dedup", dedup); Bind(document, "$status", status); Bind(document, "$expires", expires); Bind(document, "$length", length);
            document.ExecuteNonQuery();
        }
        using var posting = Command("INSERT INTO postings VALUES($term,$id,$frequency,$ordinal)");
        Bind(posting, "$term", ""); Bind(posting, "$id", id); Bind(posting, "$frequency", 0);
        Bind(posting, "$ordinal", 0);
        var ordinal = 0;
        foreach (var (term, frequency) in frequencies)
        {
            posting.Parameters["$term"].Value = term;
            posting.Parameters["$frequency"].Value = frequency;
            posting.Parameters["$ordinal"].Value = ordinal++;
            posting.ExecuteNonQuery();
        }
    }

    public void CompleteRebuild(string version)
    {
        using var command = Command("INSERT INTO generation VALUES($version,$tokenizer,$scope)");
        Bind(command, "$version", version); Bind(command, "$tokenizer", TokenizerVersion);
        Bind(command, "$scope", _scopeHash);
        command.ExecuteNonQuery();
        _build!.Commit(); _build.Dispose(); _build = null;
    }

    /// <summary>Uses the current transaction for every statement, including schema-independent rebuild commands.</summary>
    private SqliteCommand Command(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql; command.Transaction = _build;
        return command;
    }

    private void Execute(string sql) { using var command = Command(sql); command.ExecuteNonQuery(); }
    private static void Bind(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    public void Dispose() { _build?.Dispose(); _connection.Dispose(); }
}
