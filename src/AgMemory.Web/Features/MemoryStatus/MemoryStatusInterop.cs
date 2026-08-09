using Microsoft.JSInterop;

namespace AgMemory.Web.Features.MemoryStatus;

/// <summary>Typed owner of the collocated, same-origin status fetch module.</summary>
public sealed class MemoryStatusInterop : IAsyncDisposable
{
    internal const string ModulePath = "./Features/MemoryStatus/MemoryStatusPage.razor.js";
    private const string LoadMethod = "load";
    private readonly IJSRuntime _js;
    private IJSObjectReference? _module;

    public MemoryStatusInterop(IJSRuntime js) => _js = js;

    public async ValueTask<MemoryStatusApiResponse?> LoadAsync() =>
        await (await GetModuleAsync()).InvokeAsync<MemoryStatusApiResponse?>(LoadMethod);

    private async ValueTask<IJSObjectReference> GetModuleAsync() =>
        _module ??= await _js.InvokeAsync<IJSObjectReference>("import", ModulePath);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_module is not null)
                await _module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // A SignalR circuit can end while the module is in use.
        }
    }
}
