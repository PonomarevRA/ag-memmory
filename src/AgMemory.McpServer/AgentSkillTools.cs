using System.ComponentModel;
using System.Text.Json;
using AgMemory.AgentSetup;
using ModelContextProtocol.Server;

namespace AgMemory.McpServer;

/// <summary>Installed agents can discover the complete bounded skill without a Web URL, checkout or configuration changes.</summary>
[McpServerToolType]
public sealed class AgentSkillTools
{
    /// <summary>Return a bounded embedded document or an explicit protocol error for unknown resource names.</summary>
    [McpServerTool(Name = "memory_skill", ReadOnly = true, OpenWorld = false)]
    [Description("Read the bundled agmemory skill and installation guidance. Default resource SKILL.md; use manifest resource paths for knowledge workflows, installation and UI metadata. Never executes or installs content.")]
    public string Read(string resource = "SKILL.md")
    {
        var content = AgentSkillPackage.Read(resource);
        var response = content is null
            ? AgentResponse<AgentSkillDocument>.Failure("invalid_argument")
            : AgentResponse<AgentSkillDocument>.Success(new(AgentSkillPackage.Manifest(), resource, content));
        return JsonSerializer.Serialize(response);
    }
}

/// <summary>Portable text and manifest; contains no operator configuration or filesystem locations.</summary>
public sealed record AgentSkillDocument(SkillManifest Manifest, string Resource, string Content);
