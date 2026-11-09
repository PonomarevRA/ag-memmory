using System.Net;
using System.Text.Json;
using AgMemory.Contracts;
using AgMemory.Storage.LanceDb;
using AgMemory.Web.Features.Knowledge;
using AgMemory.Web.Features.MemoryGraph;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgMemory.Web.Tests;

/// <summary>Checks fixed-profile browser projection and denial before storage initialization.</summary>
public sealed class KnowledgeEndpointTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "agmemory-knowledge-web-" + Guid.NewGuid().ToString("N"));

    /// <summary>Denied environments and addresses never initialize a configured storage root.</summary>
    [Theory]
    [InlineData(false, "127.0.0.1")]
    [InlineData(true, "203.0.113.12")]
    public async Task DeniedAccess_DoesNotInitializeStorage(bool development, string address)
    {
        var feature = new LocalKnowledgeFeature(Options(), _path);
        var context = Context(address);
        var result = await KnowledgeEndpoint.HandleAsync(context, new Environment(development), feature, default);
        await result.ExecuteAsync(context);
        Assert.Contains("unavailable", ReadBody(context));
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.False(Directory.Exists(_path));
    }

    /// <summary>Web reads the same exact authority document and only returns approved public fields.</summary>
    [Fact]
    public async Task ConfiguredProfile_ReadsProceduresAndAggregatesWithoutIdentifiers()
    {
        var options = Options();
        var config = options.TryCreate(_path)!;
        var profile = KnowledgeProfileIdentity.Get(config.StoragePath, config.Actor, config.Scope);
        var store = new KnowledgeFileStore(config.StoragePath);
        var state = new KnowledgeState { Version = 1 };
        state.Procedures.Add("secret-procedure-key", new("secret-procedure-key", 1, "Download logs", [], "local", "access", "fetch logs", "check checksum", "private-runbook-url", DateTimeOffset.UnixEpoch));
        state.Applications.Add("secret-application", new("secret-application", "secret-procedure-key", 1, "local", ProcedureApplicationOutcome.ReportedSuccess, "reported", DateTimeOffset.UnixEpoch));
        state.Metadata.Add("secret-source", new("secret-source", 1, KnowledgeClass.Procedure, FoundationStatus.None, "local", null));
        Assert.True(await store.CompareExchangeAsync(profile, 0, state, default));
        var context = Context("::1");
        var result = await KnowledgeEndpoint.HandleAsync(context, new Environment(true), new(options, _path), default);
        await result.ExecuteAsync(context);
        var json = ReadBody(context);
        Assert.Contains("Download logs", json);
        Assert.Contains("ReportedSuccess", json);
        Assert.DoesNotContain("secret-", json);
        Assert.DoesNotContain("private-runbook-url", json);
        Assert.DoesNotContain(config.StoragePath, json);
        Assert.DoesNotContain(profile, json);
        Assert.Equal(1, (await store.ReadAsync(profile, default)).Version);
        using var body = JsonDocument.Parse(json);
        Assert.Equal("available", body.RootElement.GetProperty("status").GetString());
    }

    /// <summary>Identity is stable across reopened key reads and changes with server authority.</summary>
    [Fact]
    public void SharedProfileIdentity_IsStableAndAuthorityBound()
    {
        var scope = new MemoryScope(new("tenant"), null, null, null, null);
        var first = KnowledgeProfileIdentity.Get(_path, new("actor"), scope);
        var authority = JsonSerializer.Serialize(new { Actor = new ActorId("actor"), Scope = scope, StoragePath = _path });
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
            KnowledgeProfileIdentity.LoadKey(_path), System.Text.Encoding.UTF8.GetBytes(authority)))[..24];
        Assert.Equal(expected, first);
        Assert.Equal(first, KnowledgeProfileIdentity.Get(_path, new("actor"), scope));
        Assert.NotEqual(first, KnowledgeProfileIdentity.Get(_path, new("another-actor"), scope));
        Assert.NotEqual(first, KnowledgeProfileIdentity.Get(_path, new("actor"), scope with { ProjectId = new("project") }));
    }

    /// <summary>A configured team is independent of actor but never of exact scope, store or team; default identity is unchanged.</summary>
    [Fact]
    public async Task LocalTeamIdentityAndWebProjectionRespectBoundaries()
    {
        var scope = new MemoryScope(new("tenant"));
        var team = KnowledgeProfileIdentity.GetKnowledge(_path, new("codex"), scope, "team");
        Assert.Equal(team, KnowledgeProfileIdentity.GetKnowledge(_path, new("cursor"), scope, "team"));
        Assert.NotEqual(team, KnowledgeProfileIdentity.GetKnowledge(_path, new("codex"), scope, "other"));
        Assert.NotEqual(team, KnowledgeProfileIdentity.GetKnowledge(_path, new("codex"), scope with { ProjectId = new("project") }, "team"));
        Assert.NotEqual(team, KnowledgeProfileIdentity.GetKnowledge(Path.Combine(_path, "other"), new("codex"), scope, "team"));
        Assert.Equal(KnowledgeProfileIdentity.Get(_path, new("codex"), scope), KnowledgeProfileIdentity.GetKnowledge(_path, new("codex"), scope, null));
        var options = new MemoryGraphHostOptions { Enabled = true, ActorId = "browser", KnowledgeTeamId = "team", Scope = new() { TenantId = "tenant" }, StoragePath = _path };
        var state = new KnowledgeState { Version = 1 };
        state.Procedures.Add("procedure", new("procedure", 1, "Team runbook", [], "local", "access", "export", "check", null, DateTimeOffset.UtcNow));
        Assert.True(await new KnowledgeFileStore(_path).CompareExchangeAsync(team, 0, state, default));
        var response = await new LocalKnowledgeFeature(options, _path).ReadAsync(default);
        Assert.Equal("Team runbook", Assert.Single(response.Procedures).Goal);
        Assert.Throws<ArgumentException>(() => KnowledgeProfileIdentity.GetKnowledge(_path, new("actor"), scope, " "));
        Assert.Throws<ArgumentException>(() => KnowledgeProfileIdentity.GetKnowledge(_path, new("actor"), scope, new string('x', 101)));
        var invalid = new MemoryGraphHostOptions { Enabled = true, ActorId = "actor", KnowledgeTeamId = " ", Scope = new() { TenantId = "tenant" } };
        Assert.False(new LocalKnowledgeFeature(invalid, _path).IsConfigured);
    }

    /// <summary>Only current active exact-scope revisions receive utility cards; empty samples remain unknown.</summary>
    [Fact]
    public async Task CurrentSourceCards_RejectStaleExpiredInactiveAndForeignContent_AndKeepWindowsIndependent()
    {
        var now = DateTimeOffset.UtcNow;
        var scope = new MemoryScope(new("tenant"));
        var state = new KnowledgeState();
        var records = new Dictionary<string, MemoryRecord>();
        foreach (var id in new[] { "current", "stale", "expired", "inactive", "foreign", "missing" })
        {
            state.Metadata.Add(id, new(id, 1, KnowledgeClass.Knowledge, FoundationStatus.None, "local", null));
            records.Add(id, new(new(id), scope, MemoryRecordType.Fact, MemoryLifecycleStatus.Active, "safe preview", null,
                .5, .5, 4, now, now, 1, [], new("test", null, null, null, null, null, null, []), null, null, id));
        }
        records["stale"] = records["stale"] with { Version = 2 };
        records["expired"] = records["expired"] with { ExpiresAt = now };
        records["inactive"] = records["inactive"] with { Status = MemoryLifecycleStatus.Invalid };
        records["foreign"] = records["foreign"] with { Scope = new(new("another-tenant")) };
        records.Remove("missing");
        foreach (var id in new[] { "stale", "expired", "inactive", "foreign", "missing" })
        {
            var receipt = "incorrect-" + id;
            state.Presentations.Add(receipt, new(receipt, id, 1, "task", now, now.AddDays(1)));
            state.Feedback.Add(receipt, new(receipt, "task", KnowledgeFeedbackKind.Incorrect, 1, now));
        }
        state.Presentations.Add("recent", new("recent", "current", 1, "task", now.AddDays(-2), now.AddDays(1)));
        state.Feedback.Add("recent", new("recent", "task", KnowledgeFeedbackKind.Helpful, 1, now.AddDays(-2)));
        state.Presentations.Add("old", new("old", "current", 1, "other", now.AddDays(-9), now.AddDays(-8)));
        state.Feedback.Add("old", new("old", "other", KnowledgeFeedbackKind.Harmful, 1, now.AddDays(-9)));
        var eligibleSourceIds = new HashSet<string>(StringComparer.Ordinal);
        var cards = await LocalKnowledgeFeature.ReadSourcesAsync(state, scope,
            id => Task.FromResult<MemoryRecord?>(records.GetValueOrDefault(id)), now, eligibleSourceIds);
        var card = Assert.Single(cards);
        Assert.Equal("L11", card.Class);
        Assert.Null(card.Windows[0].Utility);
        Assert.Equal(0, card.Windows[0].RatingSampleSize);
        Assert.Equal(1, card.Windows[1].RatingSampleSize);
        Assert.Equal(2, card.Windows[2].RatingSampleSize);
        Assert.Equal(.5, card.Windows[2].Utility);
        Assert.DoesNotContain("current", JsonSerializer.Serialize(card));
        Assert.Empty(LocalKnowledgeFeature.Project(state, eligibleSourceIds).ReviewReasons);
        state.Presentations.Add("current-incorrect", new("current-incorrect", "current", 1, "third", now, now.AddDays(1)));
        state.Feedback.Add("current-incorrect", new("current-incorrect", "third", KnowledgeFeedbackKind.Incorrect, 1, now));
        Assert.Equal(1, LocalKnowledgeFeature.Project(state, eligibleSourceIds).ReviewReasons["reported_content_issue"]);
    }

    private MemoryGraphHostOptions Options() => new() { Enabled = true, StoragePath = Path.Combine(_path, "store"), ActorId = "private-actor", Scope = new() { TenantId = "private-tenant" } };
    private static DefaultHttpContext Context(string address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        context.Response.Body = new MemoryStream();
        return context;
    }
    private static string ReadBody(HttpContext context) => System.Text.Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    /// <summary>Removes only the isolated fixture root.</summary>
    public void Dispose() { if (Directory.Exists(_path)) Directory.Delete(_path, true); }

    private sealed class Environment(bool development) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = development ? Environments.Development : Environments.Production;
        public string ApplicationName { get; set; } = "KnowledgeTests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
