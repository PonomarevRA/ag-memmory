using System.Reflection;
using Xunit;

namespace AgMemory.Web.Tests;

public sealed class LayoutNavigationContractTests
{
    [Fact]
    public void MobileDrawer_HasKeyboardDismissalAndCannotScrollHorizontally()
    {
        var layout = Read("src/AgMemory.Web/Components/Layout/MainLayout.razor");
        var css = Read("src/AgMemory.Web/wwwroot/app.css");

        Assert.Contains("aria-controls=\"primary-navigation\"", layout, StringComparison.Ordinal);
        Assert.Contains("aria-expanded=\"@_menuOpen\"", layout, StringComparison.Ordinal);
        Assert.Contains("@onkeydown", layout, StringComparison.Ordinal);
        Assert.Contains("Escape", layout, StringComparison.Ordinal);

        var drawerRule = css[css.IndexOf("@media (max-width: 62rem)", StringComparison.Ordinal)..];
        Assert.Matches("overflow-x:\\s*(hidden|clip)", drawerRule);
    }

    [Fact]
    public void MobileDrawer_ContainsFocusAndReturnsItToTheMenuToggleAfterEscapeOrClose()
    {
        var layout = Read("src/AgMemory.Web/Components/Layout/MainLayout.razor");

        Assert.Equal(2, layout.Split("class=\"focus-sentinel\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, layout.Split("@onfocus=\"FocusMenuStartAsync\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("await _menuClose.FocusAsync();", layout, StringComparison.Ordinal);
        Assert.Contains("eventArgs.Key != \"Escape\" || !_menuOpen", layout, StringComparison.Ordinal);
        Assert.Contains("await _menuToggle.FocusAsync();", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void GraphAssets_UseOneOpaqueKeyAcrossViewsAndExposeNoLabelOrOrdinalJoin()
    {
        var page = Read("src/AgMemory.Web/Features/MemoryGraph/MemoryGraphPage.razor");
        var module = FrontendSourcePaths.ReadAbsolute(FrontendSourcePaths.MemoryGraphSanitize) +
                     FrontendSourcePaths.ReadAbsolute(FrontendSourcePaths.MemoryGraphController);

        Assert.Contains("SelectNodeAsync(node.Id)", page, StringComparison.Ordinal);
        Assert.Contains("node.layoutKey = node.id", module, StringComparison.Ordinal);
        Assert.Contains("node.href", module, StringComparison.Ordinal);
        Assert.DoesNotContain("labelOrdinal", module, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("entityName", module, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("canonical", module, StringComparison.OrdinalIgnoreCase);
    }

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(RepositoryRoot, relativePath));

    private static string RepositoryRoot
    {
        get
        {
            var current = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "ag-memory.slnx"))) return current.FullName;
                current = current.Parent;
            }

            throw new InvalidOperationException("Unable to find repository root for layout verification.");
        }
    }
}
