using System.Net;
using System.Reflection;
using System.Text.Json;
using AgMemory.Contracts;
using AgMemory.Storage.LanceDb;
using AgMemory.Web.Features.MemoryReader;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgMemory.Web.Tests;

public sealed class MemoryReaderEndpointTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly MemoryScope Scope = new(new("reader-tenant"), new("reader-project"), new("reader-workspace"), new("reader-chat"), new("reader-run"));

    [Theory]
    [InlineData(true, true, "127.0.0.1", true)]
    [InlineData(true, true, "::1", true)]
    [InlineData(false, true, "127.0.0.1", false)]
    [InlineData(true, false, "127.0.0.1", false)]
    [InlineData(true, true, "203.0.113.15", false)]
    public void StoreAccess_RequiresConfigurationDevelopmentAndLoopback(bool configured, bool development, string address, bool expected)
    {
        Assert.Equal(expected, MemoryReaderAccessPolicy.AllowsStoreAccess(configured, development, IPAddress.Parse(address)));
    }

    [Fact]
    public async Task UnconfiguredHomeEndpoint_ReturnsUnavailableWithoutOpeningStorage()
    {
        var root = TemporaryPath();
        try
        {
            await using var feature = new LocalMemoryReaderFeature(new MemoryReaderHostOptions(), root, Provider(root));
            var context = Context(IPAddress.Loopback);

            var response = await ExecuteAsync(await MemoryReaderEndpoint.HandleHomeAsync(
                context, new TestHostEnvironment(isDevelopment: true, root), feature, default), context);

            Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
            Assert.Equal("unavailable", response.Status);
            Assert.Empty(response.Blocks);
            Assert.False(Directory.Exists(Path.Combine(root, "reader-lancedb")));
        }
        finally
        {
            DeleteTemporaryPath(root);
        }
    }

    [Fact]
    public async Task DeniedEndpoint_DoesNotInitializeConfiguredReaderStorage()
    {
        var root = TemporaryPath();
        var storagePath = Path.Combine(root, "reader-lancedb");
        try
        {
            await using var feature = new LocalMemoryReaderFeature(Options(new("reader-home")), root, Provider(root));
            var context = Context(IPAddress.Parse("203.0.113.15"));

            var response = await ExecuteAsync(await MemoryReaderEndpoint.HandleHomeAsync(
                context, new TestHostEnvironment(isDevelopment: false, root), feature, default), context);

            Assert.Equal("unavailable", response.Status);
            Assert.Empty(response.Blocks);
            Assert.False(Directory.Exists(storagePath));
        }
        finally
        {
            DeleteTemporaryPath(root);
        }
    }

    [Fact]
    public async Task DevelopmentLoopbackHome_ReturnsSafeContentDtoWithoutAuthorityOrDurableIds()
    {
        var root = TemporaryPath();
        var storagePath = Path.Combine(root, "reader-lancedb");
        var homeId = new MemoryId("reader-home");
        try
        {
            Directory.CreateDirectory(root);
            await SeedAsync(storagePath, Record(homeId, "# Home ^home\nReadable local text"));
            await using var feature = new LocalMemoryReaderFeature(Options(homeId), root, Provider(root));
            var context = Context(IPAddress.Loopback);

            var response = await ExecuteAsync(await MemoryReaderEndpoint.HandleHomeAsync(
                context, new TestHostEnvironment(isDevelopment: true, root), feature, default), context);
            var json = await ReadBodyAsync(context);

            Assert.Equal("available", response.Status);
            Assert.NotNull(response.RouteKey);
            Assert.NotEqual(homeId.Value, response.RouteKey);
            var block = Assert.Single(response.Blocks);
            Assert.NotEqual("home", block.Id);
            Assert.DoesNotContain(homeId.Value, json, StringComparison.Ordinal);
            Assert.DoesNotContain("reader-actor", json, StringComparison.Ordinal);
            Assert.DoesNotContain(Scope.TenantId.Value, json, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryPath(root);
        }
    }

    [Fact]
    public async Task InvalidNavigationToken_ReturnsStaleBeforeOpeningConfiguredStorage()
    {
        var root = TemporaryPath();
        var storagePath = Path.Combine(root, "reader-lancedb");
        try
        {
            await using var feature = new LocalMemoryReaderFeature(Options(new("reader-home")), root, Provider(root));
            var context = Context(IPAddress.Loopback);

            var response = await ExecuteAsync(await MemoryReaderEndpoint.HandleDocumentAsync(
                context, "route-key", "not-a-protected-token", new TestHostEnvironment(isDevelopment: true, root), feature, default), context);

            Assert.Equal("stale", response.Status);
            Assert.Empty(response.Blocks);
            Assert.False(Directory.Exists(storagePath));
        }
        finally
        {
            DeleteTemporaryPath(root);
        }
    }

    [Fact]
    public async Task IncompatibleNavigationSchemaToken_ReturnsStaleBeforeOpeningConfiguredStorage()
    {
        var root = TemporaryPath();
        var storagePath = Path.Combine(root, "reader-lancedb");
        try
        {
            var provider = Provider(root);
            await using var feature = new LocalMemoryReaderFeature(Options(new("reader-home")), root, provider);
            var incompatibleToken = provider.CreateProtector("AgMemory.Web.MemoryReader.Navigation.v1").Protect(
                JsonSerializer.Serialize(new
                {
                    SchemaVersion = "memory-reader-navigation-v0",
                    Kind = "block",
                    RouteKey = "route-key",
                    BlockKey = "target",
                    Version = (long?)null,
                    NextBlockIndex = (int?)null
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var context = Context(IPAddress.Loopback);

            var response = await ExecuteAsync(await MemoryReaderEndpoint.HandleDocumentAsync(
                context, "route-key", incompatibleToken, new TestHostEnvironment(isDevelopment: true, root), feature, default), context);

            Assert.Equal("stale", response.Status);
            Assert.Empty(response.Blocks);
            Assert.False(Directory.Exists(storagePath));
        }
        finally
        {
            DeleteTemporaryPath(root);
        }
    }

    [Fact]
    public async Task InvalidOrMismatchedCatalogFilter_ReturnsStaleBeforeOpeningConfiguredStorage()
    {
        var root = TemporaryPath();
        var storagePath = Path.Combine(root, "reader-lancedb");
        try
        {
            var provider = Provider(root);
            await using var feature = new LocalMemoryReaderFeature(Options(new("reader-home")), root, provider);
            var context = Context(IPAddress.Loopback);

            var invalid = await ExecuteCatalogAsync(await MemoryReaderEndpoint.HandleCatalogAsync(
                context, null, "type/Fact", null, new TestHostEnvironment(isDevelopment: true, root), feature, default), context);
            Assert.Equal("stale", invalid.Status);
            Assert.False(Directory.Exists(storagePath));

            var token = feature.ProtectCatalogContinuation(new("generation", 20), new("type/fact", null));
            context = Context(IPAddress.Loopback);
            var mismatched = await ExecuteCatalogAsync(await MemoryReaderEndpoint.HandleCatalogAsync(
                context, token, null, null, new TestHostEnvironment(isDevelopment: true, root), feature, default), context);
            Assert.Equal("stale", mismatched.Status);
            Assert.False(Directory.Exists(storagePath));
        }
        finally
        {
            DeleteTemporaryPath(root);
        }
    }

    [Fact]
    public async Task DevelopmentLoopbackCatalog_ProjectsSafeFacetsAndFiltersWithoutDurableIds()
    {
        var root = TemporaryPath();
        var storagePath = Path.Combine(root, "reader-lancedb");
        var fact = new MemoryId("facet-fact");
        var outcome = new MemoryId("facet-outcome");
        try
        {
            Directory.CreateDirectory(root);
            await SeedAsync(storagePath, Record(fact, "Fact preview [[doc:engineering/outcome-title|Outcome]] [[related:engineering/outcome-title|Related outcome]]", MemoryRecordType.Fact, ["raw entity"]));
            await SeedAsync(storagePath, Record(outcome, "Outcome preview", MemoryRecordType.Outcome, ["raw entity"]));
            await using (var store = new LanceDbMemoryStore(new(storagePath)))
            {
                await store.UpsertWikiMetadataAsync(new(Scope, fact, 1, "Fact title", "engineering/core", "fact-title", ["shared tag"]), default);
                await store.UpsertWikiMetadataAsync(new(Scope, outcome, 1, "Outcome title", "engineering", "outcome-title", ["shared tag"]), default);
            }
            await using var feature = new LocalMemoryReaderFeature(Options(fact), root, Provider(root));
            var context = Context(IPAddress.Loopback);

            var response = await ExecuteCatalogAsync(await MemoryReaderEndpoint.HandleCatalogAsync(
                context, null, null, null, new TestHostEnvironment(isDevelopment: true, root), feature, default), context);
            var json = await ReadBodyAsync(context);

            Assert.Equal("available", response.Status);
            Assert.Equal(new[] { "engineering", "engineering/core" }, response.Namespaces.Select(facet => facet.Locator));
            var shared = Assert.Single(response.Tags, facet => facet.Label == "shared tag");
            Assert.Equal(2, shared.Count);
            Assert.DoesNotContain(fact.Value, json, StringComparison.Ordinal);
            Assert.DoesNotContain(outcome.Value, json, StringComparison.Ordinal);
            Assert.DoesNotContain("reader-actor", json, StringComparison.Ordinal);
            Assert.DoesNotContain(Scope.TenantId.Value, json, StringComparison.Ordinal);
            Assert.DoesNotContain("raw entity", json, StringComparison.Ordinal);

            context = Context(IPAddress.Loopback);
            var filtered = await ExecuteCatalogAsync(await MemoryReaderEndpoint.HandleCatalogAsync(
                context, null, "engineering/core", shared.Locator, new TestHostEnvironment(isDevelopment: true, root), feature, default), context);
            Assert.Equal("available", filtered.Status);
            Assert.Single(filtered.Documents);
            Assert.Equal("Fact", filtered.Documents[0].Type);
            Assert.Equal("Fact title", filtered.Documents[0].Title);

            var routeKey = filtered.Documents[0].Href.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
            context = Context(IPAddress.Loopback);
            var document = await ExecuteAsync(await MemoryReaderEndpoint.HandleDocumentAsync(
                context, routeKey, null, new TestHostEnvironment(isDevelopment: true, root), feature, default), context);
            Assert.Equal("available", document.Status);
            Assert.Equal("Fact title", document.Title);
            Assert.Equal("engineering/core", document.Namespace);
            Assert.Equal(["shared tag"], document.Tags);
            Assert.Single(document.Children);
            Assert.Equal("child", document.Children[0].Kind);
            Assert.Equal("Outcome", document.Children[0].Label);
            Assert.Single(document.Related);
            Assert.Equal("related", document.Related[0].Kind);
            Assert.Equal("Related outcome", document.Related[0].Label);

            context = Context(IPAddress.Loopback);
            var tree = await ExecuteTreeAsync(await MemoryReaderEndpoint.HandleTreeAsync(
                context, new TestHostEnvironment(isDevelopment: true, root), feature, default), context);
            Assert.Equal("available", tree.Status);
            var engineeringTree = Assert.Single(tree.Roots, node => node.Label == "engineering");
            var coreTree = Assert.Single(engineeringTree.Children, node => node.Label == "core");
            var factType = Assert.Single(coreTree.Children, node => node.Label == "Fact");
            var factNode = Assert.Single(factType.Children, node => node.Label == "Fact title");
            Assert.Contains(factNode.Children, node => node.Label == "Outcome" && node.LinkWeight == 1);

            var outcomeRouteKey = document.Children[0].Href.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
            context = Context(IPAddress.Loopback);
            var outcomeDocument = await ExecuteAsync(await MemoryReaderEndpoint.HandleDocumentAsync(
                context, outcomeRouteKey, null, new TestHostEnvironment(isDevelopment: true, root), feature, default), context);
            Assert.Single(outcomeDocument.Backlinks);
            Assert.Equal("Outcome", outcomeDocument.Backlinks[0].Label);
            Assert.Single(outcomeDocument.Related);
            Assert.Equal("Related outcome", outcomeDocument.Related[0].Label);

            await using (var verifier = new LanceDbMemoryStore(new(storagePath)))
            {
                var eligibility = new MemorySearchEligibility(new AuthorizedScopeSet([new ScopeSelector(Scope)]), null, Now.AddHours(1));
                var generation = await verifier.ReadReadyLeafPageAsync(eligibility, null, default);
                Assert.NotNull(generation);
                await UpdateAsync(verifier, Record(outcome, "Outcome preview", MemoryRecordType.Outcome, ["raw entity"]) with
                {
                    Status = MemoryLifecycleStatus.Invalid,
                    Version = 2,
                    UpdatedAt = Now.AddMinutes(1)
                }, 1);
                var staleRelations = await verifier.ReadWikiRelationsAsync(eligibility, generation!.GenerationKey, fact,
                    MemoryWikiRelationKind.Child, default);
                Assert.Empty(staleRelations);
            }
        }
        finally
        {
            DeleteTemporaryPath(root);
        }
    }

    [Fact]
    public async Task ProtectedNavigation_SurvivesProviderRestartAndIsBoundToItsRoute()
    {
        var root = TemporaryPath();
        try
        {
            string token;
            await using (var feature = new LocalMemoryReaderFeature(new MemoryReaderHostOptions(), root, Provider(root)))
            {
                token = feature.ProtectBlock("route-one", "target-block");
                Assert.False(token.Contains("route-one", StringComparison.Ordinal));
            }

            await using var reopened = new LocalMemoryReaderFeature(new MemoryReaderHostOptions(), root, Provider(root));
            Assert.True(reopened.TryUnprotectNavigation("route-one", token, out var blockKey, out var cursor));
            Assert.Equal("target-block", blockKey);
            Assert.Null(cursor);
            Assert.False(reopened.TryUnprotectNavigation("route-two", token, out _, out _));
        }
        finally
        {
            DeleteTemporaryPath(root);
        }
    }

    [Fact]
    public void BrowserDtosAndHandlers_ExcludeAuthorityRawIdsAndCursorState()
    {
        var properties = typeof(MemoryReaderApiResponse).GetProperties()
            .Concat(typeof(MemoryReaderBlockDto).GetProperties())
            .Concat(typeof(MemoryReaderInlineDto).GetProperties())
            .Concat(typeof(MemoryReaderRelationDto).GetProperties())
            .Concat(typeof(MemoryReaderCatalogApiResponse).GetProperties())
            .Concat(typeof(MemoryReaderCatalogDocumentDto).GetProperties())
            .Concat(typeof(MemoryReaderCatalogFacetDto).GetProperties())
            .Concat(typeof(MemoryReaderTreeApiResponse).GetProperties())
            .Concat(typeof(MemoryReaderTreeNodeDto).GetProperties());
        var prohibited = new[] { "Actor", "Scope", "MemoryId", "HomeMemory", "Cursor", "Storage", "Policy", "Provenance", "Embedding" };

        Assert.All(properties, property =>
            Assert.DoesNotContain(prohibited, token => property.Name.Contains(token, StringComparison.OrdinalIgnoreCase)));

        foreach (var handler in new[]
                 {
                     typeof(MemoryReaderEndpoint).GetMethod(nameof(MemoryReaderEndpoint.HandleHomeAsync), BindingFlags.Public | BindingFlags.Static)!,
                     typeof(MemoryReaderEndpoint).GetMethod(nameof(MemoryReaderEndpoint.HandleDocumentAsync), BindingFlags.Public | BindingFlags.Static)!,
                     typeof(MemoryReaderEndpoint).GetMethod(nameof(MemoryReaderEndpoint.HandleCatalogAsync), BindingFlags.Public | BindingFlags.Static)!,
                     typeof(MemoryReaderEndpoint).GetMethod(nameof(MemoryReaderEndpoint.HandleTreeAsync), BindingFlags.Public | BindingFlags.Static)!
                 })
        {
            Assert.DoesNotContain(handler.GetParameters(), parameter =>
                parameter.ParameterType == typeof(ActorId) || parameter.ParameterType == typeof(MemoryScope) ||
                parameter.ParameterType == typeof(MemoryId) || parameter.ParameterType == typeof(MemoryReaderHomeRequest) ||
                parameter.ParameterType == typeof(MemoryReaderDocumentRequest));
        }
    }

    private static MemoryReaderHostOptions Options(MemoryId homeMemoryId) => new()
    {
        Enabled = true,
        StoragePath = "reader-lancedb",
        ActorId = "reader-actor",
        HomeMemoryId = homeMemoryId.Value,
        Scope = new MemoryReaderScopeOptions
        {
            TenantId = Scope.TenantId.Value,
            ProjectId = Scope.ProjectId?.Value,
            WorkspaceId = Scope.WorkspaceId?.Value,
            ChatId = Scope.ChatId?.Value,
            RunId = Scope.RunId?.Value
        }
    };

    private static MemoryRecord Record(MemoryId id, string text, MemoryRecordType type = MemoryRecordType.Fact, IReadOnlyList<string>? entities = null) => new(
        id, Scope, type, MemoryLifecycleStatus.Active, text, null, .5, .5, 5,
        Now, Now, 1, entities ?? [], new MemoryProvenance("test", null, null, null, null, null, null, [new("evidence")]),
        null, null, $"dedup-{id.Value}");

    private static async Task SeedAsync(string storagePath, MemoryRecord record)
    {
        await using var store = new LanceDbMemoryStore(new(storagePath));
        await using var transaction = await store.BeginTransactionAsync(default);
        var scopes = new AuthorizedScopeSet([new ScopeSelector(Scope)]);
        Assert.True((await transaction.WriteRecordAsync(scopes, record, 0, default)).Applied);
        await transaction.CommitAsync(default);
    }

    private static async Task UpdateAsync(LanceDbMemoryStore store, MemoryRecord record, long expectedVersion)
    {
        await using var transaction = await store.BeginTransactionAsync(default);
        var scopes = new AuthorizedScopeSet([new ScopeSelector(Scope)]);
        Assert.True((await transaction.WriteRecordAsync(scopes, record, expectedVersion, default)).Applied);
        await transaction.CommitAsync(default);
    }

    private static IDataProtectionProvider Provider(string root) =>
        DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "data-protection")));

    private static DefaultHttpContext Context(IPAddress address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = address;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<MemoryReaderApiResponse> ExecuteAsync(IResult result, HttpContext context)
    {
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (await JsonSerializer.DeserializeAsync<MemoryReaderApiResponse>(
            context.Response.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    private static async Task<MemoryReaderCatalogApiResponse> ExecuteCatalogAsync(IResult result, HttpContext context)
    {
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (await JsonSerializer.DeserializeAsync<MemoryReaderCatalogApiResponse>(
            context.Response.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    private static async Task<MemoryReaderTreeApiResponse> ExecuteTreeAsync(IResult result, HttpContext context)
    {
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (await JsonSerializer.DeserializeAsync<MemoryReaderTreeApiResponse>(
            context.Response.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(RepositoryRoot, relativePath));

    private static string RepositoryRoot
    {
        get
        {
            var current = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "ag-memory.slnx"))) return current.FullName;
                current = current.Parent;
            }

            throw new InvalidOperationException("Unable to find the repository root for reader Web verification.");
        }
    }

    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), $"agmemory-reader-web-tests-{Guid.NewGuid():N}");

    private static void DeleteTemporaryPath(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed class TestHostEnvironment(bool isDevelopment, string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = isDevelopment ? Environments.Development : Environments.Production;
        public string ApplicationName { get; set; } = "AgMemory.Web.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
