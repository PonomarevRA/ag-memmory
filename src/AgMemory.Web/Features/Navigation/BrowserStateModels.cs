namespace AgMemory.Web.Features.Navigation;

/// <summary>Privacy-safe local visit record. It deliberately contains no query or form content.</summary>
public sealed record VisitEntry(string Route, string Title, DateTimeOffset VisitedAt);

/// <summary>Local-only transcript item, separate from durable AgMemory records.</summary>
public sealed record LocalChatMessage(string Id, string Role, string Content, DateTimeOffset CreatedAt);

public sealed record BrowserPreferences(string Theme, bool ReduceMotion);
