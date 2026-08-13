using Xunit;

namespace AgMemory.Web.Tests;

public sealed class ReconnectModalContractTests
{
    [Fact]
    public void ReconnectModal_ProvidesRussianRecoveryActionsWithoutReplacingTheFatalFallback()
    {
        var modal = FrontendSourcePaths.Read("src/AgMemory.Web/Components/Layout/ReconnectModal.razor");
        var module = FrontendSourcePaths.ReadAbsolute(FrontendSourcePaths.ReconnectModal);
        var layout = FrontendSourcePaths.Read("src/AgMemory.Web/Components/Layout/MainLayout.razor");
        var css = FrontendSourcePaths.Read("src/AgMemory.Web/wwwroot/app.css");

        Assert.Contains("components-reconnect-modal", modal, StringComparison.Ordinal);
        Assert.Contains("Подключение к интерфейсу", modal, StringComparison.Ordinal);
        Assert.Contains("components-reconnect-reload-button", modal, StringComparison.Ordinal);
        Assert.Contains("Перезагрузить страницу", modal, StringComparison.Ordinal);
        Assert.Contains("if (detail.state === 'show' && !modal.open) modal.showModal();", module, StringComparison.Ordinal);
        Assert.Contains("components-reconnect-reload-button", module, StringComparison.Ordinal);
        Assert.Equal(4, module.Split("location.reload()", StringSplitOptions.None).Length - 1);
        Assert.Contains("#components-reconnect-modal", css, StringComparison.Ordinal);
        Assert.Contains(".components-reconnect-resume-failed", css, StringComparison.Ordinal);
        Assert.Contains("id=\"blazor-error-ui\"", layout, StringComparison.Ordinal);
        Assert.Contains("href=\".\"", layout, StringComparison.Ordinal);
    }
}
