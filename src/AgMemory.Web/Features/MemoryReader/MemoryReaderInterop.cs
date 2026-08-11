using Microsoft.JSInterop;

namespace AgMemory.Web.Features.MemoryReader;

/// <summary>Typed owner of the collocated local reader fetch module.</summary>
public sealed class MemoryReaderInterop : IAsyncDisposable
{
    internal const string ModulePath = "./Features/MemoryReader/MemoryReaderPage.razor.js";
    private readonly IJSRuntime _js;
    private IJSObjectReference? _module;

    public MemoryReaderInterop(IJSRuntime js) => _js = js;

    public async ValueTask<MemoryReaderApiResponse?> LoadAsync(string? routeKey, string? token) =>
        await (await GetModuleAsync()).InvokeAsync<MemoryReaderApiResponse?>("load", routeKey, token);

    public async ValueTask<MemoryReaderCatalogApiResponse?> LoadCatalogAsync(string? token) =>
        await (await GetModuleAsync()).InvokeAsync<MemoryReaderCatalogApiResponse?>("loadCatalog", token);

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
            // The interactive circuit can end while the local reader page is open.
        }
    }
}
