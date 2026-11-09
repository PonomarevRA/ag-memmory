using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgMemory.Contracts;

namespace AgMemory.Storage.LanceDb;

/// <summary>Shares exact server-authority partition identity between local hosts without depending on an executable adapter.</summary>
public static class KnowledgeProfileIdentity
{
    /// <summary>Derives the existing opaque profile reference; inputs must come from trusted server configuration.</summary>
    public static string Get(string storagePath, ActorId actor, MemoryScope scope)
    {
        var key = LoadKey(storagePath);
        var authority = JsonSerializer.Serialize(new { Actor = actor, Scope = scope, StoragePath = storagePath });
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(authority)))[..24];
    }

    /// <summary>Validates an opt-in local team label without silently falling back to actor isolation on malformed input.</summary>
    public static void ValidateTeamId(string? teamId)
    {
        if (teamId is null) return;
        if (teamId.Length is < 1 or > 100 || !teamId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException("Knowledge team must contain 1-100 ASCII letters, digits, hyphens or underscores.", nameof(teamId));
    }

    /// <summary>Shares only knowledge state within an explicitly configured team, exact scope and store; citation authority remains actor-bound.</summary>
    public static string GetKnowledge(string storagePath, ActorId actor, MemoryScope scope, string? teamId)
    {
        ValidateTeamId(teamId);
        if (teamId is null) return Get(storagePath, actor, scope);
        var key = LoadKey(storagePath);
        var authority = JsonSerializer.Serialize(new { Purpose = "knowledge-team-v1", Team = teamId, Scope = scope, StoragePath = storagePath });
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(authority)))[..24];
    }

    /// <summary>Validates a caller's task label; a label partitions L2 within authority and never grants access.</summary>
    public static void ValidateTaskKey(string taskKey)
    {
        if (string.IsNullOrEmpty(taskKey) || taskKey.Length > 100 ||
            taskKey.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new ArgumentException("Task key must contain 1-100 ASCII letters, digits, hyphens or underscores.", nameof(taskKey));
    }

    /// <summary>Binds task L2 to the existing exact actor/team authority; the label cannot select a different profile.</summary>
    public static string GetTaskWorking(string storagePath, ActorId actor, MemoryScope scope, string? teamId, string taskKey)
    {
        ValidateTaskKey(taskKey);
        var profile = GetKnowledge(storagePath, actor, scope, teamId);
        var authority = JsonSerializer.Serialize(new { Purpose = "task-working-v1", Profile = profile, Task = taskKey });
        return Convert.ToHexStringLower(HMACSHA256.HashData(LoadKey(storagePath), Encoding.UTF8.GetBytes(authority)))[..24];
    }

    /// <summary>Publishes a private random store key atomically, rejecting linked roots, linked keys and unsafe Unix permissions.</summary>
    public static byte[] LoadKey(string storagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        if (new DirectoryInfo(storagePath).LinkTarget is not null) throw new IOException("Unsafe key directory.");
        Directory.CreateDirectory(storagePath);
        var keyPath = Path.Combine(storagePath, ".agmemory-agent-key");
        if (new FileInfo(keyPath).LinkTarget is not null) throw new IOException("Unsafe key file.");
        if (!File.Exists(keyPath))
        {
            var temporary = Path.Combine(storagePath, $".agmemory-key-{Guid.NewGuid():N}");
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var file = new FileStream(temporary, options)) { file.Write(RandomNumberGenerator.GetBytes(32)); file.Flush(true); }
                try { File.Move(temporary, keyPath, overwrite: false); }
                catch (IOException) when (File.Exists(keyPath)) { }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        if (new FileInfo(keyPath).LinkTarget is not null) throw new IOException("Unsafe key file.");
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(keyPath);
            if ((mode & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0) throw new IOException("Unsafe key permissions.");
        }
        var info = new FileInfo(keyPath);
        if (info.Length != 32) throw new IOException("Invalid key.");
        var key = File.ReadAllBytes(keyPath);
        if (key.Length != 32) throw new IOException("Invalid key.");
        return key;
    }
}
