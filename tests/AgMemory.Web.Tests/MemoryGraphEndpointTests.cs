using System.Net;
using System.Reflection;
using System.Text.Json;
using AgMemory.Web.Features.Chat;
using AgMemory.Web.Features.MemoryGraph;
using AgMemory.Web.Features.MemoryReader;
using AgMemory.Web.Features.MemoryStatus;
using AgMemory.Web.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgMemory.Web.Tests;

public sealed class MemoryGraphEndpointTests
{
    [Theory]
    [InlineData(true, true, "127.0.0.1", true)]
    [InlineData(true, true, "::1", true)]
    [InlineData(false, true, "127.0.0.1", false)]
    [InlineData(true, false, "127.0.0.1", false)]
    [InlineData(true, true, "203.0.113.15", false)]
    public void StoreAccess_RequiresEnabledConfigurationDevelopmentAndLoopback(
        bool isConfigured,
        bool isDevelopment,
        string address,
        bool expected)
    {
        var allowed = MemoryGraphAccessPolicy.AllowsStoreAccess(
            isConfigured,
            isDevelopment,
            IPAddress.Parse(address));

        Assert.Equal(expected, allowed);
    }

    [Fact]
    public async Task UnconfiguredEndpoint_ReturnsNoStoreUnavailableResponseWithoutCreatingAStore()
    {
        var root = TemporaryPath();
        try
        {
            await using var feature = new LocalMemoryGraphFeature(new MemoryGraphHostOptions(), root);
            var context = Context(IPAddress.Loopback);

            var result = await MemoryGraphEndpoint.HandleAsync(
                context,
                new TestHostEnvironment(isDevelopment: true, root),
                feature,
                default);
            var response = await ExecuteAsync(result, context);

            Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
            Assert.Equal("unavailable", response.Status);
            Assert.Empty(response.Nodes);
            Assert.Empty(response.Edges);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            DeleteTemporaryPath(root);
        }
    }

    [Fact]
    public async Task NonLoopbackOrNonDevelopmentEndpoint_DoesNotInitializeConfiguredStorage()
    {
        var root = TemporaryPath();
        var storagePath = Path.Combine(root, "lancedb");
        try
        {
            await using var feature = ConfiguredFeature(root);
            var context = Context(IPAddress.Parse("203.0.113.15"));

            var result = await MemoryGraphEndpoint.HandleAsync(
                context,
                new TestHostEnvironment(isDevelopment: false, root),
                feature,
                default);
            var response = await ExecuteAsync(result, context);

            Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
            Assert.Equal("unavailable", response.Status);
            Assert.Empty(response.Nodes);
            Assert.Empty(response.Edges);
            Assert.False(Directory.Exists(storagePath));
        }
        finally
        {
            DeleteTemporaryPath(root);
        }
    }

    [Fact]
    public async Task InvalidContinuation_ReturnsStaleBeforeOpeningConfiguredStorage()
    {
        var root = TemporaryPath();
        var storagePath = Path.Combine(root, "lancedb");
        try
        {
            await using var feature = ConfiguredFeature(root);
            var context = Context(IPAddress.Loopback);

            var response = await ExecuteAsync(await MemoryGraphEndpoint.HandleAsync(
                context, new TestHostEnvironment(isDevelopment: true, root), feature, default,
                continuation: "not-a-protected-token"), context);

            Assert.Equal("stale", response.Status);
            Assert.Empty(response.Nodes);
            Assert.Empty(response.Edges);
            Assert.False(Directory.Exists(storagePath));
        }
        finally
        {
            DeleteTemporaryPath(root);
        }
    }

    [Fact]
    public async Task IncompatibleContinuationSchema_ReturnsStaleBeforeOpeningConfiguredStorage()
    {
        var root = TemporaryPath();
        var storagePath = Path.Combine(root, "lancedb");
        try
        {
            var provider = Provider(root);
            await using var feature = new LocalMemoryGraphFeature(ConfiguredOptions(), root, provider);
            var incompatibleToken = provider.CreateProtector("AgMemory.Web.MemoryGraph.Navigation.v1").Protect(
                JsonSerializer.Serialize(new
                {
                    SchemaVersion = "memory-graph-navigation-v0",
                    GenerationKey = "generation",
                    NextPortion = 2
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var context = Context(IPAddress.Loopback);

            var response = await ExecuteAsync(await MemoryGraphEndpoint.HandleAsync(
                context, new TestHostEnvironment(isDevelopment: true, root), feature, default,
                continuation: incompatibleToken), context);

            Assert.Equal("stale", response.Status);
            Assert.Empty(response.Nodes);
            Assert.Empty(response.Edges);
            Assert.False(Directory.Exists(storagePath));
        }
        finally
        {
            DeleteTemporaryPath(root);
        }
    }

    [Fact]
    public async Task DevelopmentLoopbackEndpoint_ReadsConfiguredGraphAndReturnsSafeDto()
    {
        var root = TemporaryPath();
        var storagePath = Path.Combine(root, "lancedb");
        try
        {
            Directory.CreateDirectory(root);
            await using var feature = ConfiguredFeature(root);
            var context = Context(IPAddress.Loopback);

            var result = await MemoryGraphEndpoint.HandleAsync(
                context,
                new TestHostEnvironment(isDevelopment: true, root),
                feature,
                default);
            var response = await ExecuteAsync(result, context);

            Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
            Assert.Equal("available", response.Status);
            Assert.Empty(response.Nodes);
            Assert.Empty(response.Edges);
            Assert.True(Directory.Exists(storagePath));
        }
        finally
        {
            DeleteTemporaryPath(root);
        }
    }

    [Fact]
    public async Task ConfiguredFeature_ReadsAnEmptyNewStoreAsAnAvailableEmptySnapshot()
    {
        var root = TemporaryPath();
        try
        {
            Directory.CreateDirectory(root);
            await using var feature = ConfiguredFeature(root);

            var snapshot = await feature.ReadAsync(default);

            Assert.Null(snapshot.Error);
            Assert.Empty(snapshot.Nodes);
            Assert.Empty(snapshot.Edges);
        }
        finally
        {
            DeleteTemporaryPath(root);
        }
    }

    [Fact]
    public async Task ConfiguredFeature_ReadsAggregateStatusFromTheSharedConfiguredScope()
    {
        var root = TemporaryPath();
        try
        {
            Directory.CreateDirectory(root);
            var options = ConfiguredOptions();
            await using (var writer = new LocalChatMemoryFeature(options, root))
            {
                await writer.RememberAsync("status-thread", "user", "First status marker", default);
                await writer.RememberAsync("status-thread", "assistant", "Second status marker", default);
            }
            await using var feature = new LocalMemoryGraphFeature(options, root);

            var status = await feature.ReadStatusAsync(default);

            Assert.Equal(2, status.TotalMemoryCount);
            Assert.Equal(2, status.ActiveMemoryCount);
            Assert.Equal(0, status.ExpiredMemoryCount);
            Assert.Equal(0, status.InactiveMemoryCount);
            Assert.NotNull(status.LatestUpdateAt);
            var eventCount = Assert.Single(status.ActiveByType);
            Assert.Equal(AgMemory.Contracts.MemoryRecordType.Event, eventCount.Type);
            Assert.Equal(2, eventCount.Count);
        }
        finally
        {
            DeleteTemporaryPath(root);
        }
    }

    [Fact]
    public async Task UnconfiguredStatusEndpoint_ReturnsSafeUnavailableResponseWithoutCreatingAStore()
    {
        var root = TemporaryPath();
        try
        {
            await using var feature = new LocalMemoryGraphFeature(new MemoryGraphHostOptions(), root);
            var context = Context(IPAddress.Loopback);

            await using var chat = new LocalChatMemoryFeature(new MemoryGraphHostOptions(), root);
            await using var reader = new LocalMemoryReaderFeature(new MemoryReaderHostOptions(), root, Provider(root));
            var result = await MemoryStatusEndpoint.HandleAsync(
                context,
                new TestHostEnvironment(isDevelopment: true, root),
                feature,
                chat,
                reader,
                default);
            var response = await ExecuteStatusAsync(result, context);

            Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
            Assert.Equal("unavailable", response.Status);
            Assert.Equal(0, response.TotalMemoryCount);
            Assert.Equal(0, response.LastInjectHitCount);
            Assert.False(response.ReaderAligned);
            Assert.Empty(response.ActiveByType);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            DeleteTemporaryPath(root);
        }
    }

    [Fact]
    public async Task StatusEndpoint_ReportsInjectCountAndReaderAlignmentWithoutLeakingStoreIdentity()
    {
        var root = TemporaryPath();
        try
        {
            Directory.CreateDirectory(root);
            var options = ConfiguredOptions();
            await using var chat = new LocalChatMemoryFeature(options, root);
            await chat.RememberAsync("status-thread", "user", "alpha-beta-gamma unique marker", default);
            await chat.RecallAsync("qqqqzzzzmmmm unmatched query tokens", default);
            await using var feature = new LocalMemoryGraphFeature(options, root);
            await using var alignedReader = new LocalMemoryReaderFeature(AlignedReaderOptions(options), root, Provider(root));
            await using var driftedReader = new LocalMemoryReaderFeature(new MemoryReaderHostOptions
            {
                Enabled = true,
                StoragePath = options.StoragePath,
                ActorId = options.ActorId,
                Scope = new MemoryReaderScopeOptions { TenantId = "other-tenant" }
            }, root, Provider(root));
            var context = Context(IPAddress.Loopback);

            var aligned = await ExecuteStatusAsync(await MemoryStatusEndpoint.HandleAsync(
                context, new TestHostEnvironment(isDevelopment: true, root), feature, chat, alignedReader, default), context);
            context.Response.Body = new MemoryStream();
            var drifted = await ExecuteStatusAsync(await MemoryStatusEndpoint.HandleAsync(
                context, new TestHostEnvironment(isDevelopment: true, root), feature, chat, driftedReader, default), context);

            Assert.Equal("available", aligned.Status);
            Assert.Equal(1, aligned.LastInjectHitCount);
            Assert.True(aligned.ReaderAligned);
            Assert.False(drifted.ReaderAligned);
            Assert.DoesNotContain("Actor", typeof(MemoryStatusApiResponse).GetProperties().Select(property => property.Name));
            Assert.DoesNotContain("Scope", typeof(MemoryStatusApiResponse).GetProperties().Select(property => property.Name));
            Assert.DoesNotContain("StoragePath", typeof(MemoryStatusApiResponse).GetProperties().Select(property => property.Name));
        }
        finally
        {
            DeleteTemporaryPath(root);
        }
    }

    [Fact]
    public async Task ConfiguredFeature_DefaultsStorageToThePersistentApplicationDirectory()
    {
        var root = TemporaryPath();
        var storagePath = Path.Combine(root, "memory-graph.lancedb");
        try
        {
            Directory.CreateDirectory(root);
            await using var feature = new LocalMemoryGraphFeature(new MemoryGraphHostOptions
            {
                Enabled = true,
                ActorId = "local-graph-actor",
                Scope = new MemoryGraphScopeOptions { TenantId = "local-tenant" }
            }, root);

            var snapshot = await feature.ReadAsync(default);

            Assert.Null(snapshot.Error);
            Assert.True(Directory.Exists(storagePath));
        }
        finally
        {
            DeleteTemporaryPath(root);
        }
    }

    [Fact]
    public void ExplicitApplicationDataDirectoryIsResolvedOutsideTheBundle()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agmemory-data-{Guid.NewGuid():N}");

        var resolved = LocalApplicationPaths.ResolveDataDirectory(root);

        Assert.Equal(Path.GetFullPath(root), resolved);
    }

    [Fact]
    public async Task DevelopmentSettings_EnableTheExplicitLocalGraphScope()
    {
        var webAssemblyDirectory = Path.GetDirectoryName(typeof(Program).Assembly.Location)!;
        var developmentSettings = Path.Combine(webAssemblyDirectory, "appsettings.Development.json");
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(developmentSettings, optional: false)
            .Build();

        var options = configuration.GetSection(MemoryGraphHostOptions.SectionName).Get<MemoryGraphHostOptions>();

        Assert.NotNull(options);
        Assert.True(options.Enabled);
        Assert.Equal("local-development", options.ActorId);
        Assert.Equal("local-development", options.Scope?.TenantId);

        var root = TemporaryPath();
        try
        {
            await using var feature = new LocalMemoryGraphFeature(options, root);
            Assert.True(feature.IsConfigured);
        }
        finally
        {
            DeleteTemporaryPath(root);
        }
    }

    [Fact]
    public void BrowserDtos_ExcludeActorScopeRecordAndErrorValues()
    {
        var responseProperties = typeof(MemoryGraphApiResponse).GetProperties();
        var nodeProperties = typeof(MemoryGraphNodeDto).GetProperties();
        var edgeProperties = typeof(MemoryGraphEdgeDto).GetProperties();
        var prohibited = new[] { "Actor", "Scope", "MemoryId", "Record", "Canonical", "Entity", "Error", "Provenance" };

        Assert.All(responseProperties.Concat(nodeProperties).Concat(edgeProperties), property =>
            Assert.DoesNotContain(prohibited, token => property.Name.Contains(token, StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(nodeProperties, property => property.Name == nameof(MemoryGraphNodeDto.Id) && property.PropertyType == typeof(string));
        Assert.All(edgeProperties, property => Assert.True(
            property.PropertyType == typeof(string) || property.PropertyType == typeof(int),
            $"Unexpected browser DTO field type for {property.Name}."));
    }

    [Fact]
    public void StatusBrowserDtos_ExcludeMemoryContentAndAuthorityValues()
    {
        var properties = typeof(MemoryStatusApiResponse).GetProperties()
            .Concat(typeof(MemoryStatusTypeCountDto).GetProperties());
        var prohibited = new[] { "Actor", "Scope", "MemoryId", "Record", "Canonical", "Entity", "Error", "Provenance", "Content" };

        Assert.All(properties, property =>
            Assert.DoesNotContain(prohibited, token => property.Name.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Endpoint_AcceptsNoBrowserSuppliedActorOrScope()
    {
        var handler = typeof(MemoryGraphEndpoint).GetMethod(nameof(MemoryGraphEndpoint.HandleAsync), BindingFlags.Public | BindingFlags.Static)!;
        var parameterTypes = handler.GetParameters().Select(parameter => parameter.ParameterType).ToArray();

        Assert.Equal("/api/memory-graph", MemoryGraphEndpoint.Route);
        Assert.DoesNotContain(parameterTypes, type =>
            type.Name.Contains("Actor", StringComparison.OrdinalIgnoreCase) ||
            type.Name.Contains("Scope", StringComparison.OrdinalIgnoreCase) ||
            type.Name.Contains("MemoryGraphRequest", StringComparison.Ordinal));
    }

    [Fact]
    public void StatusEndpoint_AcceptsNoBrowserSuppliedActorOrScope()
    {
        var handler = typeof(MemoryStatusEndpoint).GetMethod(nameof(MemoryStatusEndpoint.HandleAsync), BindingFlags.Public | BindingFlags.Static)!;
        var parameterTypes = handler.GetParameters().Select(parameter => parameter.ParameterType).ToArray();

        Assert.Equal("/api/memory-status", MemoryStatusEndpoint.Route);
        Assert.DoesNotContain(parameterTypes, type =>
            type.Name.Contains("Actor", StringComparison.OrdinalIgnoreCase) ||
            type.Name.Contains("Scope", StringComparison.OrdinalIgnoreCase) ||
            type.Name.Contains("MemoryStatus", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Feature_RequiresCompleteServerOwnedConfigurationBeforeItCanBeAvailable()
    {
        var root = TemporaryPath();
        try
        {
            await using var incomplete = new LocalMemoryGraphFeature(new MemoryGraphHostOptions
            {
                Enabled = true,
                StoragePath = "lancedb",
                Scope = new MemoryGraphScopeOptions { TenantId = "local-tenant" }
            }, root);
            await using var configured = ConfiguredFeature(root);

            Assert.False(incomplete.IsConfigured);
            Assert.True(configured.IsConfigured);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            DeleteTemporaryPath(root);
        }
    }

    private static LocalMemoryGraphFeature ConfiguredFeature(string root) => new(ConfiguredOptions(), root);

    private static IDataProtectionProvider Provider(string root) =>
        DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "data-protection")));

    private static MemoryReaderHostOptions AlignedReaderOptions(MemoryGraphHostOptions options) => new()
    {
        Enabled = true,
        StoragePath = options.StoragePath,
        ActorId = options.ActorId,
        Scope = new MemoryReaderScopeOptions
        {
            TenantId = options.Scope?.TenantId,
            ProjectId = options.Scope?.ProjectId,
            WorkspaceId = options.Scope?.WorkspaceId,
            ChatId = options.Scope?.ChatId,
            RunId = options.Scope?.RunId
        }
    };

    private static MemoryGraphHostOptions ConfiguredOptions() => new()
        {
            Enabled = true,
            StoragePath = "lancedb",
            ActorId = "local-graph-actor",
            Scope = new MemoryGraphScopeOptions
            {
                TenantId = "local-tenant",
                ProjectId = "local-project",
                WorkspaceId = "local-workspace",
                ChatId = "local-chat",
                RunId = "local-run"
            }
        };

    private static DefaultHttpContext Context(IPAddress address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = address;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<MemoryGraphApiResponse> ExecuteAsync(IResult result, HttpContext context)
    {
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (await JsonSerializer.DeserializeAsync<MemoryGraphApiResponse>(
            context.Response.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    private static async Task<MemoryStatusApiResponse> ExecuteStatusAsync(IResult result, HttpContext context)
    {
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (await JsonSerializer.DeserializeAsync<MemoryStatusApiResponse>(
            context.Response.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), $"agmemory-graph-web-tests-{Guid.NewGuid():N}");

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
