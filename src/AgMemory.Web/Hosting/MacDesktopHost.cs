using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Globalization;
using AgMemory.DesktopSupport;

namespace AgMemory.Web.Hosting;

/// <summary>Enables a loopback-only browser host when the executable is launched from a macOS app bundle.</summary>
public sealed class MacDesktopHost
{
    private const string DesktopModeEnvironmentVariable = "AGMEMORY_DESKTOP_MODE";

    private MacDesktopHost(bool isEnabled, bool isNativeWindow) { IsEnabled = isEnabled; IsNativeWindow = isNativeWindow; }

    public bool IsEnabled { get; }
    public bool IsNativeWindow { get; }

    /// <summary>Detects the existing Mac shell or explicit Windows/Linux embedded owner without changing browser-mode defaults.</summary>
    public static MacDesktopHost Detect(string? baseDirectory = null, bool? isMacOS = null)
    {
        var enabledByEnvironment = string.Equals(
            Environment.GetEnvironmentVariable(DesktopModeEnvironmentVariable),
            "1",
            StringComparison.Ordinal);
        var onMacOS = isMacOS ?? OperatingSystem.IsMacOS();
        var directory = baseDirectory ?? AppContext.BaseDirectory;
        var inApplicationBundle = directory.Contains(".app/Contents/MacOS", StringComparison.Ordinal);
        var nativeWindow = onMacOS && Environment.GetEnvironmentVariable("AGMEMORY_NATIVE_WINDOW") == "1"
            || Environment.GetEnvironmentVariable("AGMEMORY_DESKTOP_EMBEDDED") == "1";
        return new(enabledByEnvironment || (onMacOS && inApplicationBundle) || nativeWindow, nativeWindow);
    }

    public void ConfigureLoopbackKestrel(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (IsEnabled)
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, SelectPort(PreferredPort())));
    }

    /// <summary>Accepts an explicit canonical transport override (zero means ephemeral), otherwise installer preference or 8891.</summary>
    internal static int PreferredPort()
    {
        var value = Environment.GetEnvironmentVariable("AGMEMORY_DESKTOP_PORT");
        if (value is not null)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 0 or > 65535 || port.ToString(CultureInfo.InvariantCulture) != value)
                throw new InvalidOperationException("Invalid desktop port configuration.");
            return port;
        }
        return DesktopInstallation.Read()?.PreferredPort ?? 8891;
    }

    /// <summary>Falls back to an owned ephemeral listener when the preferred port is busy; never adopts the occupant.
    /// The final Kestrel bind remains authoritative; a later bind race fails startup rather than selecting an unrelated server.</summary>
    internal static int SelectPort(int preferred)
    {
        if (preferred == 0) return 0;
        var probe = new TcpListener(IPAddress.Loopback, preferred);
        try { probe.Start(1); return preferred; }
        catch (SocketException error) when (error.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied) { return 0; }
        finally { probe.Stop(); }
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

            if (IsNativeWindow)
            {
                var line = NativeReadyLine(address);
                if (line is not null) Console.WriteLine(line);
                return;
            }
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
    /// <summary>Announces only this child's started IPv4 loopback origin; no data, query or authority is exported.</summary>
    internal static string? NativeReadyLine(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme != "http" || url.Host != "127.0.0.1" ||
            url.Port is < 1 or > 65535 || url.AbsolutePath != "/" || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0) return null;
        return "AGMEMORY_DESKTOP_URL=" + url.GetLeftPart(UriPartial.Authority) + "/";
    }

}
