namespace AgMemory.Web.Gateway;

/// <summary>Safe, provider-neutral states surfaced to the chat interface.</summary>
public enum ChatGatewayErrorCode
{
    InvalidInput,
    Unavailable,
    Unauthorized,
    RateLimited,
    Timeout,
    Interrupted
}

public enum ChatGatewayEventKind { Text, Error, Complete }

/// <summary>Browser-safe chat event that intentionally excludes provider diagnostics.</summary>
public sealed record ChatGatewayEvent(
    ChatGatewayEventKind Kind,
    string? Text = null,
    ChatGatewayErrorCode? ErrorCode = null,
    bool IsDemo = false);

/// <summary>Browser request containing only an opaque local thread id and current prompt.</summary>
public sealed record ChatApiRequest(string ThreadId, string Prompt);

public sealed record ChatGatewayRequest(string ThreadId, string Prompt);

public interface IModelChatGateway
{
    ChatGatewayStatus GetStatus();

    IAsyncEnumerable<ChatGatewayEvent> StreamAsync(
        ChatGatewayRequest request,
        CancellationToken cancellationToken);
}

public sealed record ChatGatewayStatus(bool IsAvailable, bool IsDemo, string Label);
