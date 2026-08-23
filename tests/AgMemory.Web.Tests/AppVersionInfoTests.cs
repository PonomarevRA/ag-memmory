using System.Reflection;
using System.Reflection.Emit;
using AgMemory.Web.Features.AppVersion;
using Xunit;

namespace AgMemory.Web.Tests;

public sealed class AppVersionInfoTests
{
    [Fact]
    public void From_uses_informational_product_and_strips_path_like_suffix()
    {
        var assembly = AssemblyWithInformationalVersion("1.1.0+deadbeef /tmp/build");
        var info = AppVersionInfo.From(assembly);
        Assert.Equal("1.1.0", info.Version);
        Assert.Equal("1.1.0+deadbeef", info.InformationalVersion);
    }

    [Fact]
    public void From_web_assembly_matches_the_product_version()
    {
        var info = AppVersionInfo.From(typeof(Program).Assembly);
        Assert.Equal("1.2.0", info.Version);
        Assert.StartsWith("1.2.0", info.InformationalVersion, StringComparison.Ordinal);
    }

    private static Assembly AssemblyWithInformationalVersion(string informationalVersion)
    {
        var name = new AssemblyName($"AppVersionTest_{Guid.NewGuid():N}");
        var builder = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);
        builder.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!,
            [informationalVersion]));
        builder.DefineDynamicModule("main");
        return builder;
    }
}
