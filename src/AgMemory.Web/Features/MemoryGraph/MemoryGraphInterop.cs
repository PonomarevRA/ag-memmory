using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace AgMemory.Web.Features.MemoryGraph;

/// <summary>Typed owner of the collocated local graph-rendering module.</summary>
public sealed class MemoryGraphInterop : IAsyncDisposable
{
    internal const string ModulePath = "./Features/MemoryGraph/MemoryGraphPage.razor.js";
    private const string InitializeMethod = "initialize";
    private const string LoadMethod = "load";
    private const string RenderMethod = "render";
    private const string ZoomInMethod = "zoomIn";
    private const string ZoomOutMethod = "zoomOut";
    private const string ResetMethod = "reset";
    private const string SelectNodeMethod = "selectNode";
    private const string DisposeMethod = "dispose";

    private readonly IJSRuntime _js;
    private IJSObjectReference? _module;

    public MemoryGraphInterop(IJSRuntime js) => _js = js;

    public async ValueTask InitializeAsync(
        ElementReference canvas,
        ElementReference fallbackCanvas,
        ElementReference selectionSummary) =>
        await (await GetModuleAsync()).InvokeVoidAsync(InitializeMethod, canvas, fallbackCanvas, selectionSummary);

    public async ValueTask<MemoryGraphApiResponse?> LoadAsync() =>
        await (await GetModuleAsync()).InvokeAsync<MemoryGraphApiResponse?>(LoadMethod);

    public async ValueTask RenderAsync(MemoryGraphApiResponse response) =>
        await (await GetModuleAsync()).InvokeVoidAsync(RenderMethod, response);

    public async ValueTask ZoomInAsync() =>
        await (await GetModuleAsync()).InvokeVoidAsync(ZoomInMethod);

    public async ValueTask ZoomOutAsync() =>
        await (await GetModuleAsync()).InvokeVoidAsync(ZoomOutMethod);

    public async ValueTask ResetAsync() =>
        await (await GetModuleAsync()).InvokeVoidAsync(ResetMethod);

    public async ValueTask SelectNodeAsync(string responseLocalOpaqueId) =>
        await (await GetModuleAsync()).InvokeVoidAsync(SelectNodeMethod, responseLocalOpaqueId);

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
            // A SignalR circuit can end while the local renderer is attached to the canvas.
        }
    }
}
