using AgMemory.Contracts;
using AgMemory.Storage.LanceDb;
using AgMemory.Web.Features.Chat;
using AgMemory.Web.Features.MemoryGraph;
using AgMemory.Web.Features.MemoryReader;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System.Net;
using Xunit;

namespace AgMemory.Web.Tests;

/// <summary>Saved chat citations revalidate the original source revision and fixed reader authority.</summary>
public sealed class ChatMemorySourceLinksTests
{
    private static readonly MemoryScope Scope = new(new("source-tenant"));
    private static readonly ActorId Actor = new("source-actor");

    /// <summary>A real stored Summary fallback keeps the same source guarantees as a lexical match.</summary>
    [Fact]
    public async Task StoredHandoffFallbackKeepsItsSourceLink()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agmemory-chat-source-{Guid.NewGuid():N}");
        try
        {
            var links = new ChatMemorySourceLinks(Options(root), root, new EphemeralDataProtectionProvider());
            await using var feature = new LocalChatMemoryFeature(new MemoryGraphHostOptions
            {
                Enabled = true, StoragePath = root, ActorId = Actor.Value,
                Scope = new MemoryGraphScopeOptions { TenantId = Scope.TenantId.Value }
            }, root, links);
            await feature.RememberAsync("summary-source", "assistant", "Persisted useful handoff", MemoryRecordType.Summary, default);
            var recall = await feature.RecallDetailedAsync("zzqqvvxx unmatched", default);
            var component = Assert.Single(Assert.IsType<LocalChatRecallResult>(recall).Components);
            var href = Assert.IsType<string>(component.Href);
            Assert.NotNull(component.CapturedAt);
            Assert.True(component.Revision > 0);
            Assert.NotNull(await links.ResolveAsync(href[(href.LastIndexOf('/') + 1)..], default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    /// <summary>A current opaque citation opens its area, while a later revision or tampered reference is neutral.</summary>
    [Fact]
    public async Task ResolvesOnlyTheCapturedActiveRevision()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agmemory-chat-source-{Guid.NewGuid():N}");
        try
        {
            var clock = new SourceClock();
            var source = new MemoryGraphHostConfiguration(Actor, Scope, root);
            var links = new ChatMemorySourceLinks(Options(root), root, new EphemeralDataProtectionProvider(), clock);
            var record = Record(clock.GetUtcNow());
            await using var store = new LanceDbMemoryStore(new(root));
            await WriteAsync(store, record, 0);
            var href = Assert.IsType<string>(links.Create(record, source));
            Assert.DoesNotContain(record.Id.Value, href, StringComparison.Ordinal);
            Assert.DoesNotContain(root, href, StringComparison.Ordinal);
            var token = href[(href.LastIndexOf('/') + 1)..];
            var route = Assert.IsType<string>(await links.ResolveAsync(token, default));
            Assert.StartsWith("/memory-reader/", route, StringComparison.Ordinal);
            Assert.EndsWith("?area=default", route, StringComparison.Ordinal);
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            var redirect = Assert.IsType<RedirectHttpResult>(await ChatMemorySourceLinks.HandleAsync(context, token,
                new SourceEnvironment("Development"), links, default));
            Assert.Equal(route, redirect.Url);
            Assert.False(redirect.Permanent);
            Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
            Assert.Null(await links.ResolveAsync(token + "tampered", default));

            await WriteAsync(store, record with { Version = 2, UpdatedAt = clock.GetUtcNow().AddSeconds(1) }, 1);
            Assert.Null(await links.ResolveAsync(token, default));
            var unavailable = await ChatMemorySourceLinks.HandleAsync(context, token, new SourceEnvironment("Development"), links, default);
            Assert.Equal(410, Assert.IsAssignableFrom<IStatusCodeHttpResult>(unavailable).StatusCode);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    /// <summary>The public handler rejects remote/production requests and never caches or opens storage for malformed tokens.</summary>
    [Theory]
    [InlineData("127.0.0.1", "Development", 410)]
    [InlineData("127.0.0.1", "Production", 403)]
    [InlineData("203.0.113.8", "Development", 403)]
    public async Task HttpBoundaryIsLocalAndDoesNotCacheFailures(string remote, string environment, int status)
    {
        var root = Path.Combine(Path.GetTempPath(), $"agmemory-chat-source-{Guid.NewGuid():N}");
        var links = new ChatMemorySourceLinks(Options(root), root, new EphemeralDataProtectionProvider());
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        var result = await ChatMemorySourceLinks.HandleAsync(context, "malformed-reference", new SourceEnvironment(environment), links, default);
        Assert.Equal(status, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.False(Directory.Exists(root));
    }

    /// <summary>References expire and cannot be reused when an area is rebound to a different scope or chat actor.</summary>
    [Fact]
    public async Task RejectsExpiryAndReconfiguredAuthorityWithoutOpeningStorage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agmemory-chat-source-{Guid.NewGuid():N}");
        var clock = new SourceClock();
        var provider = new EphemeralDataProtectionProvider();
        var links = new ChatMemorySourceLinks(Options(root), root, provider, clock);
        var source = new MemoryGraphHostConfiguration(Actor, Scope, root);
        var record = Record(clock.GetUtcNow());
        var href = Assert.IsType<string>(links.Create(record, source));
        var token = href[(href.LastIndexOf('/') + 1)..];
        Assert.Null(links.Create(record, source with { Actor = new("other-actor") }));
        Assert.Null(links.Create(record with { Status = MemoryLifecycleStatus.Invalid }, source));
        var changed = new ChatMemorySourceLinks(new MemoryReaderHostOptions
        {
            Enabled = true, StoragePath = root, ActorId = Actor.Value,
            Scope = new MemoryReaderScopeOptions { TenantId = "other-tenant" }
        }, root, provider, clock);
        Assert.Null(await changed.ResolveAsync(token, default));
        clock.Now = clock.Now.AddDays(2);
        Assert.Null(await links.ResolveAsync(token, default));
        Assert.False(Directory.Exists(root));
    }

    /// <summary>Creates an exact configured reader without relying on development defaults.</summary>
    private static MemoryReaderHostOptions Options(string root) => new()
    {
        Enabled = true, StoragePath = root, ActorId = Actor.Value,
        Scope = new MemoryReaderScopeOptions { TenantId = Scope.TenantId.Value }
    };

    /// <summary>Creates a source without embeddings or client-supplied routing information.</summary>
    private static MemoryRecord Record(DateTimeOffset now) => new(new("private-source-entry"), Scope,
        MemoryRecordType.Fact, MemoryLifecycleStatus.Active, "Verified source preview", null, .6, .8, 1,
        now, now, 1, [], new("source-test", null, null, null, null, null, null, []), null, null, "source-test-dedup");

    /// <summary>Applies an expected-version write to an isolated test store.</summary>
    private static async Task WriteAsync(LanceDbMemoryStore store, MemoryRecord record, long expected)
    {
        await using var transaction = await store.BeginTransactionAsync(default);
        var result = await transaction.WriteRecordsAsync(new AuthorizedScopeSet([new(Scope)]), [new(record, expected)], default);
        Assert.True(Assert.Single(result).Applied);
        await transaction.CommitAsync(default);
    }

    private sealed class SourceClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 21, 6, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class SourceEnvironment(string environment) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environment;
        public string ApplicationName { get; set; } = "source-test";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
