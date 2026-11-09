using System.Net;
using System.Text.Json;
using AgMemory.Core;
using AgMemory.Web.Features.MemoryReader;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgMemory.Web.Tests;

public sealed class MemoryReaderCardTests
{
    [Theory]
    [InlineData("", "default", "127.0.0.1", true)]
    [InlineData("route", null, "127.0.0.1", true)]
    [InlineData("route", "foreign", "127.0.0.1", true)]
    [InlineData("bad/route", "default", "127.0.0.1", true)]
    [InlineData("route", "default", "203.0.113.15", true)]
    [InlineData("route", "default", "127.0.0.1", false)]
    public async Task UnavailableRequestsDoNotInitializeStore(string routes, string? area, string address, bool development)
    {
        var root = Path.Combine(Path.GetTempPath(), $"agmemory-card-http-{Guid.NewGuid():N}");
        await using var reader = new LocalMemoryReaderFeature(new MemoryReaderHostOptions
        {
            Enabled = true, ActorId = "cards", Scope = new() { TenantId = "cards" }, StoragePath = Path.Combine(root, "store")
        }, root, new EphemeralDataProtectionProvider());
        var context = new DefaultHttpContext(); context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        context.Response.Body = new MemoryStream();
        // Results.Json requires the ordinary HTTP JSON options service in direct handler tests.
        using var services = new ServiceCollection().AddOptions().AddLogging().BuildServiceProvider();
        context.RequestServices = services;
        var result = await MemoryReaderCardsEndpoint.HandleAsync(context, routes, area, new Host(development), reader, default);
        await result.ExecuteAsync(context); context.Response.Body.Position = 0;
        var response = await JsonSerializer.DeserializeAsync<MemoryReaderCardsResponse>(context.Response.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("unavailable", response!.Status); Assert.Empty(response.Cards);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.False(Directory.Exists(Path.Combine(root, "store")));
    }

    [Fact]
    public void CardTextKeepsLiteralContentBoundedAndSkipsBlankLeadingLines()
    {
        var text = MemoryReaderCardText.Format("\r\n   \n# Meaningful heading\n<script>literal</script> " + new string('я', 10000), 160, 320);
        Assert.Equal("Meaningful heading", text.Title);
        Assert.Contains("<script>literal</script>", text.Preview);
        Assert.Equal(320, text.Preview.Length); Assert.EndsWith("…", text.Preview);
        Assert.True(MemoryReaderCardText.Format(new string('x', 5000), 160, 320).Title.Length <= 160);
        var properties = typeof(AgMemory.Contracts.MemoryReaderCard).GetProperties().Select(property => property.Name).ToArray();
        Assert.Equal(new[] { "RouteKey", "Title", "Preview", "Version" }, properties);
    }

    private sealed class Host(bool development) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = development ? Environments.Development : Environments.Production;
        public string ApplicationName { get; set; } = "AgMemory.Web.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
