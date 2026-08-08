using Microsoft.JSInterop;
using AgMemory.Web.Gateway;

namespace AgMemory.Web.Features.Chat;

/// <summary>Typed owner of the collocated chat-streaming module.</summary>
public sealed class ChatInterop : IAsyncDisposable
{
    internal const string ModulePath = "./Features/Chat/ChatPage.razor.js";
    private const string BeginMethod = "begin";
    private const string StopMethod = "stop";
    private const string DisposeMethod = "dispose";

    private readonly IJSRuntime _js;
    private IJSObjectReference? _module;

    public ChatInterop(IJSRuntime js) => _js = js;

    public async ValueTask BeginAsync(ChatApiRequest request, DotNetObjectReference<ChatPage> receiver) =>
        await (await GetModuleAsync()).InvokeVoidAsync(BeginMethod, request, receiver);

    public async ValueTask StopAsync() =>
        await (await GetModuleAsync()).InvokeVoidAsync(StopMethod);

    private async ValueTask<IJSObjectReference> GetModuleAsync() =>
        _module ??= await _js.InvokeAsync<IJSObjectReference>("import", ModulePath);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_module is not null)
            {
                await _module.InvokeVoidAsync(DisposeMethod);
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // The server circuit can end before an in-flight stream releases its browser controller.
        }
    }
}
