using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace AgMemory.AgentSetup;

/// <summary>Immutable installation resources embedded in both standalone compositions; no checkout or network is required.</summary>
public static class AgentSkillPackage
{
    public const string Version = "2.1.0";
    private static readonly string[] Names = ["SKILL.md", "references/knowledge-workflows.md", "references/installation.md", "agents/openai.yaml"];
    private static readonly IReadOnlyDictionary<string, string> Texts = Load();
    private static readonly Lazy<byte[]> Zip = new(BuildArchive);
    public static SkillDiscovery Discovery { get; } = new("agmemory", Version, "memory_skill", "/api/agent-setup", "/agent-setup.md");

    /// <summary>Return only allowlisted text; caller values never become filesystem paths or resource names.</summary>
    public static string? Read(string name) => Texts.GetValueOrDefault(name);

    /// <summary>Describe exact versioned text hashes and fixed download routes without disclosing local configuration.</summary>
    public static SkillManifest Manifest()
    {
        var resources = new SkillResource[Names.Length];
        for (var index = 0; index < Names.Length; index++)
        {
            var name = Names[index];
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Texts[name])));
            resources[index] = new(name, "/api/agent-setup/resources/" + name, hash);
        }
        return new("agmemory-skill/v1", "agmemory", Version, "/api/agent-setup/skill", "/api/agent-setup/skill.zip", resources);
    }

    /// <summary>Return a copy of the cached deterministic ZIP so HTTP clients cannot alter the embedded package.</summary>
    public static byte[] Archive() => (byte[])Zip.Value.Clone();

    /// <summary>Plain discovery document is available to agents without JavaScript or any configured memory store.</summary>
    public static string Guide() => "# AgMemory agent setup\n\nManifest: /api/agent-setup\nSkill: /api/agent-setup/skill\nComplete ZIP: /api/agent-setup/skill.zip\n\n" + Texts["references/installation.md"];

    /// <summary>Fail startup on an incomplete or oversized embedded package rather than falling back to a checkout.</summary>
    private static IReadOnlyDictionary<string, string> Load()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var assembly = typeof(AgentSkillPackage).Assembly;
        foreach (var name in Names)
        {
            using var stream = assembly.GetManifestResourceStream("AgMemory.Skill." + name)
                ?? throw new InvalidOperationException("Bundled agent skill is incomplete.");
            if (stream.Length > 65536) throw new InvalidOperationException("Bundled agent skill exceeds its text budget.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
            values.Add(name, reader.ReadToEnd());
        }
        return values;
    }

    /// <summary>Fixed entry order and timestamps make downloads reproducible across calls.</summary>
    private static byte[] BuildArchive()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in Names)
            {
                var entry = archive.CreateEntry("agmemory/" + name, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(Texts[name]);
            }
        }
        return buffer.ToArray();
    }
}

/// <summary>Bounded discovery links shared by HTTP and MCP capabilities.</summary>
public sealed record SkillDiscovery(string Name, string Version, string Operation, string ManifestPath, string GuidePath);
/// <summary>Versioned package with per-resource hashes for installation verification.</summary>
public sealed record SkillManifest(string SchemaVersion, string Name, string Version, string EntryPoint, string Archive, SkillResource[] Resources);
/// <summary>One allowlisted relative resource and its UTF-8 SHA-256.</summary>
public sealed record SkillResource(string Path, string Url, string Sha256);
