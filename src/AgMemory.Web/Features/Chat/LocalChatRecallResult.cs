using AgMemory.Web.Gateway;

namespace AgMemory.Web.Features.Chat;

public sealed record LocalChatRecallResult(
    IReadOnlyList<ChatMemoryComponent> Components,
    string? LlmContext);
