using AgMemory.AgentSetup;

namespace AgMemory.Web.Features.AgentSetup;

/// <summary>Public read-only installation help contains trusted bundled text, never scope or operator configuration.</summary>
public static class AgentSetupEndpoint
{
    /// <summary>Expose complete offline skill resources through fixed paths, independently of store availability.</summary>
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/agent-setup", () => Results.Json(AgentSkillPackage.Manifest()))
            .WithName("GetAgentSkillManifest").Produces<SkillManifest>();
        app.MapGet("/.well-known/agmemory", () => Results.Json(AgentSkillPackage.Discovery))
            .WithName("DiscoverAgMemorySkill").Produces<SkillDiscovery>();
        app.MapGet("/agent-setup.md", () => Results.Text(AgentSkillPackage.Guide(), "text/markdown; charset=utf-8"));
        app.MapGet("/api/agent-setup/skill", () => Results.Text(AgentSkillPackage.Read("SKILL.md"), "text/markdown; charset=utf-8"));
        app.MapGet("/api/agent-setup/skill.zip", () => Results.File(AgentSkillPackage.Archive(), "application/zip", "agmemory-skill-" + AgentSkillPackage.Version + ".zip"));
        app.MapGet("/api/agent-setup/resources/{**resource}", Resource).Produces(StatusCodes.Status404NotFound);
    }

    /// <summary>Reject unknown or traversal-like names instead of opening arbitrary resources or local paths.</summary>
    public static IResult Resource(string resource)
    {
        var content = AgentSkillPackage.Read(resource);
        return content is null ? Results.NotFound() : Results.Text(content, "text/plain; charset=utf-8");
    }
}
