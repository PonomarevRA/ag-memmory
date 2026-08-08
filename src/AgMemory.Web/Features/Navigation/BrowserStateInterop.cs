using Microsoft.JSInterop;

namespace AgMemory.Web.Features.Navigation;

/// <summary>Typed owner of the collocated browser-state module and its server-circuit lifecycle.</summary>
public sealed class BrowserStateInterop : IAsyncDisposable
{
    internal const string ModulePath = "./Features/Navigation/NavigationTracker.razor.js";
    private const string InitializeMethod = "initialize";
    private const string TrackVisitMethod = "trackVisit";
    private const string LoadVisitsMethod = "loadVisits";
    private const string RemoveVisitMethod = "removeVisit";
    private const string ClearVisitsMethod = "clearVisits";
    private const string LoadThreadMethod = "loadThread";
    private const string SaveThreadMethod = "saveThread";
    private const string ClearThreadMethod = "clearThread";
    private const string GetPreferencesMethod = "getPreferences";
    private const string SavePreferencesMethod = "savePreferences";
    private const string GoBackMethod = "goBack";
    private const string IsStorageAvailableMethod = "isStorageAvailable";
    private const string DisposeMethod = "dispose";

    private readonly IJSRuntime _js;
    private IJSObjectReference? _module;

    public BrowserStateInterop(IJSRuntime js) => _js = js;

    public async ValueTask InitializeAsync(string route) =>
        await (await GetModuleAsync()).InvokeVoidAsync(InitializeMethod, SanitizeRoute(route));

    public async ValueTask TrackVisitAsync(string route, string title) =>
        await (await GetModuleAsync()).InvokeVoidAsync(TrackVisitMethod, SanitizeRoute(route), title);

    public async ValueTask<IReadOnlyList<VisitEntry>> LoadVisitsAsync() =>
        await (await GetModuleAsync()).InvokeAsync<VisitEntry[]>(LoadVisitsMethod) ?? [];

    public async ValueTask RemoveVisitAsync(string route) =>
        await (await GetModuleAsync()).InvokeVoidAsync(RemoveVisitMethod, SanitizeRoute(route));

    public async ValueTask ClearVisitsAsync() =>
        await (await GetModuleAsync()).InvokeVoidAsync(ClearVisitsMethod);

    public async ValueTask<IReadOnlyList<LocalChatMessage>> LoadThreadAsync(string threadId) =>
        await (await GetModuleAsync()).InvokeAsync<LocalChatMessage[]>(LoadThreadMethod, SanitizeThreadId(threadId)) ?? [];

    public async ValueTask SaveThreadAsync(string threadId, IReadOnlyList<LocalChatMessage> messages) =>
        await (await GetModuleAsync()).InvokeVoidAsync(SaveThreadMethod, SanitizeThreadId(threadId), messages);

    public async ValueTask ClearThreadAsync(string threadId) =>
        await (await GetModuleAsync()).InvokeVoidAsync(ClearThreadMethod, SanitizeThreadId(threadId));

    public async ValueTask<BrowserPreferences> GetPreferencesAsync() =>
        await (await GetModuleAsync()).InvokeAsync<BrowserPreferences>(GetPreferencesMethod)
        ?? new BrowserPreferences("system", false);

    public async ValueTask SavePreferencesAsync(BrowserPreferences preferences) =>
        await (await GetModuleAsync()).InvokeVoidAsync(SavePreferencesMethod, preferences);

    public async ValueTask<bool> GoBackAsync() =>
        await (await GetModuleAsync()).InvokeAsync<bool>(GoBackMethod);

    public async ValueTask<bool> IsStorageAvailableAsync() =>
        await (await GetModuleAsync()).InvokeAsync<bool>(IsStorageAvailableMethod);

    private async ValueTask<IJSObjectReference> GetModuleAsync() =>
        _module ??= await _js.InvokeAsync<IJSObjectReference>("import", ModulePath);

    public static string SanitizeRoute(string route)
    {
        var withoutQuery = route.Split(['?', '#'], 2)[0].Trim();
        return string.IsNullOrWhiteSpace(withoutQuery) ? "/" :
            withoutQuery.StartsWith("/", StringComparison.Ordinal) ? withoutQuery : $"/{withoutQuery}";
    }

    public static string SanitizeThreadId(string threadId) =>
        Guid.TryParseExact(threadId, "N", out _) ? threadId : throw new ArgumentException("Thread id must be an opaque GUID.", nameof(threadId));

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
            // The circuit can close before browser-state cleanup runs.
        }
    }
}
