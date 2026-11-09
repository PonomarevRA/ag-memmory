namespace AgMemory.Web.Hosting;

/// <summary>Observes only the private inherited owner pipe in packaged Windows/Linux mode, never a public shutdown endpoint.</summary>
public static class DesktopOwnerLifetime
{
    /// <summary>An embedded launcher keeps stdin open; its EOF (or unexpected input) requests bounded host drain.</summary>
    public static void Register(WebApplication app)
    {
        if (Environment.GetEnvironmentVariable("AGMEMORY_DESKTOP_EMBEDDED") != "1" || Environment.GetEnvironmentVariable("AGMEMORY_DESKTOP_STDIN_EXIT") != "1") return;
        app.Lifetime.ApplicationStarted.Register(() => _ = WaitForOwnerAsync(Console.In, app.Lifetime.StopApplication, app.Lifetime.ApplicationStopping));
    }

    /// <summary>Reads at most one character, treating pipe closure as loss of this backend's sole window owner.</summary>
    internal static async Task WaitForOwnerAsync(TextReader input, Action stop, CancellationToken cancellationToken)
    {
        try { await input.ReadAsync(new char[1], cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (IOException) { /* A broken inherited pipe also means the owner is gone. */ }
        if (!cancellationToken.IsCancellationRequested) stop();
    }
}
