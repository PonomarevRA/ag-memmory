using System.Security.Cryptography;
using System.Text.Json;
using AgMemory.Storage.LanceDb;

namespace AgMemory.McpServer;

/// <summary>Encrypts bounded references with a store-local private key; tokens survive process restarts and carry no readable IDs.</summary>
internal sealed class AgentReferenceProtector
{
    private readonly byte[] _key;
    public string ProfileRef { get; }
    internal sealed record Reference(string Id, long Revision, long Expires, string Profile);

    public AgentReferenceProtector(LocalAgMemoryMcpConfiguration configuration)
    {
        _key = KnowledgeProfileIdentity.LoadKey(configuration.StoragePath);
        ProfileRef = KnowledgeProfileIdentity.Get(configuration.StoragePath, configuration.Actor, configuration.Scope);
    }

    /// <summary>Issues an authenticated, encrypted reference for at most one day, also bounded by source expiry.</summary>
    public string Issue(string id, long revision, DateTimeOffset? sourceExpiry, DateTimeOffset now)
    {
        var expiry = now.AddHours(24);
        if (sourceExpiry < expiry) expiry = sourceExpiry.Value;
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new Reference(id, revision, expiry.ToUnixTimeSeconds(), ProfileRef));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. ciphertext]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Rejects malformed, expired and foreign references with one indistinguishable result.</summary>
    public Reference? Read(string? token, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 1024) return null;
        try
        {
            var encoded = token.Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
            if (bytes.Length < 29) return null;
            var plaintext = new byte[bytes.Length - 28];
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plaintext);
            var reference = JsonSerializer.Deserialize<Reference>(plaintext);
            if (reference is null || reference.Profile != ProfileRef || reference.Expires <= now.ToUnixTimeSeconds()) return null;
            return reference;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or JsonException or ArgumentException)
        { return null; }
    }

}
