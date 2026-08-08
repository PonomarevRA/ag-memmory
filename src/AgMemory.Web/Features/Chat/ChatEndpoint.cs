using System.Text.Json;
using System.Text;
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
        LocalChatMemoryFeature memory,
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

        if (!memory.IsConfigured)
        {
            await WriteEventAsync(context, new(ChatGatewayEventKind.Error, ErrorCode: ChatGatewayErrorCode.Unavailable), cancellationToken);
            return;
        }

        try
        {
            await memory.RememberAsync(request!.ThreadId, "user", request.Prompt, cancellationToken).ConfigureAwait(false);
            var memoryContext = await memory.RecallAsync(request.Prompt, cancellationToken).ConfigureAwait(false);
            var answer = new StringBuilder();
            await foreach (var item in gateway.StreamAsync(new(request.ThreadId, request.Prompt, memoryContext), cancellationToken))
            {
                if (item.Kind == ChatGatewayEventKind.Text && item.Text is not null) answer.Append(item.Text);
                await WriteEventAsync(context, item, cancellationToken);
            }

            if (answer.Length > 0)
                await memory.RememberAsync(request.ThreadId, "assistant", answer.ToString(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await WriteEventAsync(context, new(ChatGatewayEventKind.Error, ErrorCode: ChatGatewayErrorCode.Unavailable), cancellationToken);
        }
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
