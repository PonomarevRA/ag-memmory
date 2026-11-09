using AgMemory.Web.Hosting;
using Xunit;

namespace AgMemory.Web.Tests;

/// <summary>Checks the native child's readiness contract without opening a browser or changing the installed application.</summary>
public sealed class MacDesktopHostTests
{
    [Theory]
    [InlineData("http://127.0.0.1:12345", "AGMEMORY_DESKTOP_URL=http://127.0.0.1:12345/")]
    [InlineData("http://127.0.0.1:12345/", "AGMEMORY_DESKTOP_URL=http://127.0.0.1:12345/")]
    [InlineData("https://127.0.0.1:12345/", null)]
    [InlineData("http://localhost:12345/", null)]
    [InlineData("http://127.0.0.1:0/", null)]
    [InlineData("http://127.0.0.1:12345/?key=secret", null)]
    [InlineData("http://user@127.0.0.1:12345/", null)]
    [InlineData("http://127.0.0.1:12345/path", null)]
    [InlineData("http://127.0.0.1:12345/#fragment", null)]
    public void ReadinessExportsOnlyTheStartedLoopbackOrigin(string address, string? expected) =>
        Assert.Equal(expected, MacDesktopHost.NativeReadyLine(address));

    [Fact]
    public void NativeModeIsMacOnlyAndPreservesBundledDesktopDetection()
    {
        var previous = Environment.GetEnvironmentVariable("AGMEMORY_NATIVE_WINDOW");
        var desktop = Environment.GetEnvironmentVariable("AGMEMORY_DESKTOP_MODE");
        try
        {
            Environment.SetEnvironmentVariable("AGMEMORY_DESKTOP_MODE", null);
            Environment.SetEnvironmentVariable("AGMEMORY_NATIVE_WINDOW", "1");
            var native = MacDesktopHost.Detect("/tmp/isolated/", true);
            Assert.True(native.IsEnabled); Assert.True(native.IsNativeWindow);
            Assert.False(MacDesktopHost.Detect("/tmp/isolated/", false).IsNativeWindow);
            Environment.SetEnvironmentVariable("AGMEMORY_NATIVE_WINDOW", null);
            var bundle = MacDesktopHost.Detect("/tmp/AgMemory.app/Contents/MacOS/", true);
            Assert.True(bundle.IsEnabled); Assert.False(bundle.IsNativeWindow);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGMEMORY_NATIVE_WINDOW", previous);
            Environment.SetEnvironmentVariable("AGMEMORY_DESKTOP_MODE", desktop);
        }
    }
}
