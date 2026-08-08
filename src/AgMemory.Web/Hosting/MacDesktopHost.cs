using System.Diagnostics;
using System.Net;

namespace AgMemory.Web.Hosting;

/// <summary>Enables a loopback-only browser host when the executable is launched from a macOS app bundle.</summary>
public sealed class MacDesktopHost
{
    private const string DesktopModeEnvironmentVariable = "AGMEMORY_DESKTOP_MODE";

    private MacDesktopHost(bool isEnabled) => IsEnabled = isEnabled;

    public bool IsEnabled { get; }

    public static MacDesktopHost Detect(string? baseDirectory = null, bool? isMacOS = null)
    {
        var enabledByEnvironment = string.Equals(
            Environment.GetEnvironmentVariable(DesktopModeEnvironmentVariable),
            "1",
            StringComparison.Ordinal);
        var onMacOS = isMacOS ?? OperatingSystem.IsMacOS();
        var directory = baseDirectory ?? AppContext.BaseDirectory;
        var inApplicationBundle = directory.Contains(".app/Contents/MacOS", StringComparison.Ordinal);
        return new(enabledByEnvironment || (onMacOS && inApplicationBundle));
    }

    public void ConfigureLoopbackKestrel(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (IsEnabled)
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
    }

    public void OpenBrowserWhenStarted(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!IsEnabled)
            return;

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var address = app.Urls.FirstOrDefault(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
            if (address is null)
                return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList = { address }
                });
            }
            catch
            {
                // A browser is a convenience for the local bundle; host availability must not depend on it.
            }
        });
    }
}
