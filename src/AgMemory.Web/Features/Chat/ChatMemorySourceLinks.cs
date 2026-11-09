using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgMemory.Contracts;
using AgMemory.Storage.LanceDb;
using AgMemory.Web.Features.MemoryGraph;
using AgMemory.Web.Features.MemoryReader;
using Microsoft.AspNetCore.DataProtection;

namespace AgMemory.Web.Features.Chat;

/// <summary>Issues local opaque citations and rechecks their revision and exact configured authority before navigation.</summary>
public sealed class ChatMemorySourceLinks
{
    public const string Route = "/api/chat-memory/sources/{token}";
    private readonly IReadOnlyList<MemoryReaderAreaConfiguration> _areas;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;

    /// <summary>Uses only server-owned reader areas; optional clock permits deterministic expiry tests.</summary>
    public ChatMemorySourceLinks(MemoryReaderHostOptions options, string dataDirectory,
        IDataProtectionProvider protection, TimeProvider? clock = null)
    {
        _areas = options.TryCreateAreas(dataDirectory);
        _protector = protection.CreateProtector("AgMemory.ChatMemorySources.v1");
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Creates a one-day reference only when the chat source exactly matches an authorised reader area.</summary>
    internal string? Create(MemoryRecord record, MemoryGraphHostConfiguration source)
    {
        if (record.Scope != source.Scope) return null;
        var area = _areas.FirstOrDefault(candidate => candidate.Configuration.Actor == source.Actor &&
            candidate.Configuration.Scope == source.Scope && SamePath(candidate.Configuration.StoragePath, source.StoragePath));
        if (area is null) return null;
        var expires = _clock.GetUtcNow().AddDays(1);
        if (record.ExpiresAt is { } recordExpiry && recordExpiry < expires) expires = recordExpiry;
        if (record.Status != MemoryLifecycleStatus.Active || expires <= _clock.GetUtcNow()) return null;
        var payload = new SourceReference(area.Id, Authority(area), record.Id.Value, record.Version, expires);
        return $"/api/chat-memory/sources/{_protector.Protect(JsonSerializer.Serialize(payload))}";
    }

    /// <summary>Resolves a protected reference to a safe Wiki route, rejecting changed, expired or differently configured sources alike.</summary>
    internal async Task<string?> ResolveAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096) return null;
        SourceReference? reference;
        try { reference = JsonSerializer.Deserialize<SourceReference>(_protector.Unprotect(token)); }
        catch (Exception error) when (error is CryptographicException or JsonException) { return null; }
        if (reference is null || reference.ExpiresAt <= _clock.GetUtcNow() || reference.Revision <= 0) return null;
        var area = _areas.FirstOrDefault(candidate => candidate.Id == reference.Area && Authority(candidate) == reference.Authority);
        if (area is null) return null;
        var configuration = area.Configuration;
        await using var store = new LanceDbMemoryStore(new(configuration.StoragePath));
        var record = await store.GetAsync(new AuthorizedScopeSet([new ScopeSelector(configuration.Scope)]),
            new MemoryId(reference.MemoryId), cancellationToken).ConfigureAwait(false);
        if (record is null || record.Version != reference.Revision || record.Status != MemoryLifecycleStatus.Active ||
            record.ExpiresAt is { } expiry && expiry <= _clock.GetUtcNow()) return null;
        var route = await store.GetOrCreateRouteAsync(record.Id, cancellationToken).ConfigureAwait(false);
        return $"/memory-reader/{Uri.EscapeDataString(route.RouteKey)}?area={Uri.EscapeDataString(area.Id)}";
    }

    /// <summary>Allows only local development readers and uses one neutral response for expired or unavailable source links.</summary>
    public static async Task<IResult> HandleAsync(HttpContext context, string token, IHostEnvironment environment,
        ChatMemorySourceLinks sources, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!MemoryGraphAccessPolicy.AllowsStoreAccess(sources._areas.Count > 0, environment.IsDevelopment(), context.Connection.RemoteIpAddress))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        try
        {
            var href = await sources.ResolveAsync(token, cancellationToken).ConfigureAwait(false);
            if (href is not null) return Results.Redirect(href);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { /* Store failures must not expose native diagnostics through a saved citation. */ }
        return Results.Content("Источник изменён или недоступен. Вернитесь к ответу и обновите контекст памяти.",
            "text/plain; charset=utf-8", statusCode: StatusCodes.Status410Gone);
    }

    /// <summary>Binds a reference to the exact server configuration even when a safe area label is reused.</summary>
    private static string Authority(MemoryReaderAreaConfiguration area) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            area.Configuration.Actor, area.Configuration.Scope, Path = Path.GetFullPath(area.Configuration.StoragePath)
        }))));

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record SourceReference(string Area, string Authority, string MemoryId, long Revision, DateTimeOffset ExpiresAt);
}
