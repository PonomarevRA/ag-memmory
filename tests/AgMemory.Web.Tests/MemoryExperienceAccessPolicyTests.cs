using System.Net;
using AgMemory.Contracts;
using AgMemory.Web.Features.MemoryExperience;
using AgMemory.Web.Features.MemoryReader;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgMemory.Web.Tests;

public sealed class MemoryExperienceAccessPolicyTests
{
    [Fact]
    public void Default_settings_keep_each_operational_view_disabled()
    {
        var webAssemblyDirectory = Path.GetDirectoryName(typeof(Program).Assembly.Location)!;
        var settings = Path.Combine(webAssemblyDirectory, "appsettings.json");
        var configuration = new ConfigurationBuilder().AddJsonFile(settings, optional: false).Build();
        var options = configuration.GetSection(MemoryExperienceOptions.SectionName).Get<MemoryExperienceOptions>();

        Assert.NotNull(options);
        Assert.False(options.RecordBrowserEnabled);
        Assert.False(options.RawRecordTextEnabled);
        Assert.False(options.UsageDashboardEnabled);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void Enabled_feature_is_allowed_for_development_loopback(string address)
    {
        Assert.True(MemoryExperienceAccessPolicy.Allows(true, true, IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData(false, true, "127.0.0.1")]
    [InlineData(true, false, "127.0.0.1")]
    [InlineData(true, true, "203.0.113.15")]
    public void Disabled_production_or_external_requests_are_rejected(bool flagEnabled, bool isDevelopment, string address)
    {
        Assert.False(MemoryExperienceAccessPolicy.Allows(flagEnabled, isDevelopment, IPAddress.Parse(address)));
    }

    [Fact]
    public void Missing_remote_address_is_rejected()
    {
        Assert.False(MemoryExperienceAccessPolicy.Allows(true, true, null));
    }

    [Fact]
    public void Raw_record_text_requires_both_the_browser_and_raw_text_flags()
    {
        var rawTextOnly = new MemoryExperienceOptions { RawRecordTextEnabled = true };
        var bothEnabled = new MemoryExperienceOptions { RecordBrowserEnabled = true, RawRecordTextEnabled = true };

        Assert.False(MemoryExperienceAccessPolicy.AllowsRawRecordText(rawTextOnly, true, IPAddress.Loopback));
        Assert.True(MemoryExperienceAccessPolicy.AllowsRawRecordText(bothEnabled, true, IPAddress.Loopback));
    }

    [Fact]
    public async Task RecordBrowser_SetsNoStore_AndDeniesBeforeOpeningStorage()
    {
        var root = TemporaryPath();
        try
        {
            await using var feature = new LocalMemoryReaderFeature(ReaderOptions(), root, Provider(root));
            var context = Context(IPAddress.Loopback);
            var result = await MemoryRecordBrowserEndpoint.HandleAsync(context, null, null, null, null, null, null, null,
                new TestHostEnvironment(true, root), Options.Create(new MemoryExperienceOptions()), feature, default);

            await result.ExecuteAsync(context);

            Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
            Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
            Assert.False(Directory.Exists(Path.Combine(root, "records-db")));
        }
        finally { DeleteTemporaryPath(root); }
    }

    [Fact]
    public async Task RecordBrowser_RejectsUnknownAreaAndTamperedContinuationBeforeStoreAccess()
    {
        var root = TemporaryPath();
        try
        {
            await using var feature = new LocalMemoryReaderFeature(ReaderOptions(), root, Provider(root));
            var enabled = Options.Create(new MemoryExperienceOptions { RecordBrowserEnabled = true });
            var environment = new TestHostEnvironment(true, root);
            var unknownContext = Context(IPAddress.Loopback);
            var unknown = await MemoryRecordBrowserEndpoint.HandleAsync(unknownContext, null, null, null, null, null, null, "other", environment, enabled, feature, default);
            await unknown.ExecuteAsync(unknownContext);
            var tamperedContext = Context(IPAddress.Loopback);
            var tampered = await MemoryRecordBrowserEndpoint.HandleAsync(tamperedContext, "not-a-protected-token", null, null, null, null, null, "default", environment, enabled, feature, default);
            await tampered.ExecuteAsync(tamperedContext);

            Assert.Equal(StatusCodes.Status404NotFound, unknownContext.Response.StatusCode);
            Assert.Equal(StatusCodes.Status404NotFound, tamperedContext.Response.StatusCode);
            Assert.False(Directory.Exists(Path.Combine(root, "records-db")));
        }
        finally { DeleteTemporaryPath(root); }
    }

    [Fact]
    public void RecordBrowserProjection_ContainsNoRouteOrDurableIdentifier()
    {
        var properties = typeof(MemoryRecordBrowserItem).GetProperties();
        Assert.DoesNotContain(properties, property => property.Name.Contains("Key", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Id", StringComparison.OrdinalIgnoreCase) || property.PropertyType == typeof(MemoryId));
    }

    private static MemoryReaderHostOptions ReaderOptions() => new()
    {
        Enabled = true, StoragePath = "records-db", ActorId = "reader-actor",
        Scope = new MemoryReaderScopeOptions { TenantId = "tenant", ProjectId = "project", WorkspaceId = "workspace", ChatId = "chat", RunId = "run" }
    };
    private static DefaultHttpContext Context(IPAddress address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = address;
        context.Response.Body = new MemoryStream();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        return context;
    }
    private static IDataProtectionProvider Provider(string root) => DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "data-protection")));
    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), $"agmemory-experience-{Guid.NewGuid():N}");
    private static void DeleteTemporaryPath(string path) { if (Directory.Exists(path)) Directory.Delete(path, true); }
    private sealed class TestHostEnvironment(bool development, string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = development ? Environments.Development : Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
