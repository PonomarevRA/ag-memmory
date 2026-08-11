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
            .Concat(typeof(MemoryReaderInlineDto).GetProperties());
        var prohibited = new[] { "Actor", "Scope", "MemoryId", "HomeMemory", "Cursor", "Storage", "Policy", "Provenance", "Embedding" };

        Assert.All(properties, property =>
            Assert.DoesNotContain(prohibited, token => property.Name.Contains(token, StringComparison.OrdinalIgnoreCase)));

        foreach (var handler in new[]
                 {
                     typeof(MemoryReaderEndpoint).GetMethod(nameof(MemoryReaderEndpoint.HandleHomeAsync), BindingFlags.Public | BindingFlags.Static)!,
                     typeof(MemoryReaderEndpoint).GetMethod(nameof(MemoryReaderEndpoint.HandleDocumentAsync), BindingFlags.Public | BindingFlags.Static)!
                 })
        {
            Assert.DoesNotContain(handler.GetParameters(), parameter =>
                parameter.ParameterType == typeof(ActorId) || parameter.ParameterType == typeof(MemoryScope) ||
                parameter.ParameterType == typeof(MemoryId) || parameter.ParameterType == typeof(MemoryReaderHomeRequest) ||
                parameter.ParameterType == typeof(MemoryReaderDocumentRequest));
        }
    }

    [Fact]
    public void ReaderPageAssets_KeepCursorAndAuthorityOutOfBrowserState()
    {
        var page = Read("src/AgMemory.Web/Features/MemoryReader/MemoryReaderPage.razor");
        var module = Read("src/AgMemory.Web/Features/MemoryReader/MemoryReaderPage.razor.js");

        Assert.Contains("@page \"/memory-reader\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("MemoryReaderIndex", page, StringComparison.Ordinal);
        Assert.DoesNotContain("actor", module, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scope", module, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("memoryId", module, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cursor", module, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("const MAX_BLOCKS = 8;", module, StringComparison.Ordinal);
    }

    [Fact]
    public void ReaderPage_AppendsContinuationPagesAndRestartsAtTheCatalogWithoutExposingNavigationState()
    {
        var page = Read("src/AgMemory.Web/Features/MemoryReader/MemoryReaderPage.razor");
        var module = Read("src/AgMemory.Web/Features/MemoryReader/MemoryReaderPage.razor.js");

        Assert.Contains("await LoadAsync(routeKey, token, append: true);", page, StringComparison.Ordinal);
        Assert.Contains("Blocks = _reader.Blocks.Concat(page.Blocks).GroupBy(block => block.Id, StringComparer.Ordinal).Select(group => group.First()).ToArray()", page, StringComparison.Ordinal);
        Assert.Contains("private async Task RestartAsync() => await LoadCatalogAsync(null, replace: true);", page, StringComparison.Ordinal);
        Assert.Contains("await LoadCatalogAsync(token, replace: false);", page, StringComparison.Ordinal);
        Assert.Contains("Documents = _catalog.Documents.Concat(page.Documents).GroupBy(document => document.Href, StringComparer.Ordinal).Select(group => group.First()).ToArray()", page, StringComparison.Ordinal);
        Assert.Contains("export async function loadCatalog(token)", module, StringComparison.Ordinal);
        Assert.DoesNotContain("continuation:", page, StringComparison.OrdinalIgnoreCase);
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

    private static MemoryRecord Record(MemoryId id, string text) => new(
        id, Scope, MemoryRecordType.Fact, MemoryLifecycleStatus.Active, text, null, .5, .5, 5,
        Now, Now, 1, [], new MemoryProvenance("test", null, null, null, null, null, null, [new("evidence")]),
        null, null, $"dedup-{id.Value}");

    private static async Task SeedAsync(string storagePath, MemoryRecord record)
    {
        await using var store = new LanceDbMemoryStore(new(storagePath));
        await using var transaction = await store.BeginTransactionAsync(default);
        var scopes = new AuthorizedScopeSet([new ScopeSelector(Scope)]);
        Assert.True((await transaction.WriteRecordAsync(scopes, record, 0, default)).Applied);
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
