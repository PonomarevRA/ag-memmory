namespace AgMemory.Web.Gateway;

/// <summary>Server-only configuration for a provider-neutral model gateway.</summary>
public sealed class ModelGatewayOptions
{
    public const string SectionName = "ModelGateway";

    public string Mode { get; init; } = "Disabled";
    public string? Endpoint { get; init; }
    public string? Model { get; init; }
    public string? ApiKey { get; init; }
    public int MaxOutputTokens { get; init; } = 512;
    public bool DisableThinking { get; init; }

    public bool HasOpenAiCompatibleSettings =>
        Uri.TryCreate(Endpoint, UriKind.Absolute, out _) &&
        !string.IsNullOrWhiteSpace(Model) &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        MaxOutputTokens is > 0 and <= 4_096;
}
