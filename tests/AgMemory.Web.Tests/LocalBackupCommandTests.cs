using AgMemory.Web.Features.Backups;
using Xunit;

namespace AgMemory.Web.Tests;

/// <summary>Headless commands do not start Web and reject ambiguous or incomplete restore requests.</summary>
public sealed class LocalBackupCommandTests
{
    [Theory]
    [InlineData("--backup", true)]
    [InlineData("--restore-backup", true)]
    [InlineData("--urls", false)]
    public void RecognizesOnlyExplicitCommands(string value, bool expected) =>
        Assert.Equal(expected, LocalBackupCommand.IsCommand([value]));

    [Theory]
    [InlineData("--restore-backup")]
    [InlineData("--backup", "--unexpected", "value")]
    [InlineData("--restore-backup", "fixture.zip")]
    public async Task InvalidCommandsFailWithoutOpeningData(params string[] args) =>
        Assert.Equal(1, await LocalBackupCommand.RunAsync(args));
}
