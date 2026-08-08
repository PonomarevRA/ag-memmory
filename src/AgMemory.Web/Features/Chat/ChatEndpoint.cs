using System.Text.Json;
using AgMemory.Web.Gateway;
using Microsoft.AspNetCore.Antiforgery;

namespace AgMemory.Web.Features.Chat;

/// <summary>Same-origin streaming endpoint. Provider configuration stays on the server.</summary>
public static class ChatEndpoint
{
    public const string Route = "/api/chat";
    public const string RateLimitPolicy = "chat";
    public const int MaximumPromptLength = 4_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task HandleAsync(
        HttpContext context,
        ChatApiRequest? request,
        IModelChatGateway gateway,
        IHostEnvironment environment,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "application/x-ndjson; charset=utf-8";

        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteEventAsync(context, new(ChatGatewayEventKind.Error, ErrorCode: ChatGatewayErrorCode.InvalidInput), cancellationToken);
            return;
        }

        if (!IsValid(request))
        {
            await WriteEventAsync(context, new(ChatGatewayEventKind.Error, ErrorCode: ChatGatewayErrorCode.InvalidInput), cancellationToken);
            return;
        }

        if (!ChatAccessPolicy.AllowsGatewayInvocation(
                gateway.GetStatus(), environment.IsDevelopment(), context.Connection.RemoteIpAddress))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await WriteEventAsync(context, new(ChatGatewayEventKind.Error, ErrorCode: ChatGatewayErrorCode.Unauthorized), cancellationToken);
            return;
        }

        await foreach (var item in gateway.StreamAsync(new(request!.ThreadId, request.Prompt), cancellationToken))
            await WriteEventAsync(context, item, cancellationToken);
    }

    private static bool IsValid(ChatApiRequest? request) =>
        request is not null &&
        request.ThreadId.Length is > 0 and <= 80 &&
        request.Prompt.Length is > 0 and <= MaximumPromptLength &&
        request.Prompt.All(character => !char.IsControl(character) || character is '\r' or '\n' or '\t');

    private static async Task WriteEventAsync(HttpContext context, ChatGatewayEvent item, CancellationToken cancellationToken)
    {
        await context.Response.WriteAsync(JsonSerializer.Serialize(item, JsonOptions), cancellationToken);
        await context.Response.WriteAsync("\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }
}
