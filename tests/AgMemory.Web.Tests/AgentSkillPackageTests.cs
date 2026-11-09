using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgMemory.AgentSetup;
using AgMemory.Web.Features.AgentSetup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgMemory.Web.Tests;

/// <summary>Offline installation resources remain complete, reproducible and independent of the memory store.</summary>
public sealed class AgentSkillPackageTests
{
    /// <summary>Every downloaded file must match the manifest, including references needed after installation.</summary>
    [Fact]
    public void ArchiveMatchesAllManifestResourcesAndDoesNotShareMutableBytes()
    {
        var manifest = AgentSkillPackage.Manifest();
        var bytes = AgentSkillPackage.Archive();
        Assert.Equal(bytes, AgentSkillPackage.Archive());
        using var archive = new ZipArchive(new MemoryStream(bytes));
        Assert.Equal(4, archive.Entries.Count);
        foreach (var resource in manifest.Resources)
        {
            var entry = archive.GetEntry("agmemory/" + resource.Path);
            Assert.NotNull(entry);
            using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false, true));
            var text = reader.ReadToEnd();
            Assert.Equal(AgentSkillPackage.Read(resource.Path), text);
            Assert.Equal(resource.Sha256, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
        }
        bytes[0] = 0;
        Assert.Equal((byte)'P', AgentSkillPackage.Archive()[0]);
    }

    /// <summary>Caller-provided paths cannot select arbitrary files or embedded resources.</summary>
    [Theory]
    [InlineData("../appsettings.json")]
    [InlineData("/etc/passwd")]
    [InlineData("references/../../SKILL.md")]
    [InlineData("skill.md")]
    public void UnknownResourceIsRejected(string name) => Assert.Null(AgentSkillPackage.Read(name));

    /// <summary>Real HTTP routing works without any store configuration; missing resources stay 404 instead of SPA fallback.</summary>
    [Fact]
    public async Task HttpDiscoveryProvidesCompleteOfflinePackageAndRejectsUnknownPaths()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        await using var app = builder.Build();
        AgentSetupEndpoint.Map(app);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
            using var manifest = JsonDocument.Parse(await client.GetStringAsync("/api/agent-setup"));
            Assert.Equal("agmemory-skill/v1", manifest.RootElement.GetProperty("schemaVersion").GetString());
            Assert.Equal(4, manifest.RootElement.GetProperty("resources").GetArrayLength());
            Assert.Contains("memory_skill", await client.GetStringAsync("/.well-known/agmemory"));
            Assert.Contains("Codex", await client.GetStringAsync("/agent-setup.md"));
            Assert.Equal(AgentSkillPackage.Read("SKILL.md"), await client.GetStringAsync("/api/agent-setup/skill"));
            using var zip = await client.GetAsync("/api/agent-setup/skill.zip");
            Assert.Equal("application/zip", zip.Content.Headers.ContentType!.MediaType);
            Assert.Contains("2.1.0", zip.Content.Headers.ContentDisposition!.FileName);
            Assert.Equal(AgentSkillPackage.Archive(), await zip.Content.ReadAsByteArrayAsync());
            foreach (var resource in AgentSkillPackage.Manifest().Resources)
                Assert.Equal(AgentSkillPackage.Read(resource.Path), await client.GetStringAsync(resource.Url));
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/agent-setup/resources/not-a-resource")).StatusCode);
        }
        finally { await app.StopAsync(); }
    }
}
