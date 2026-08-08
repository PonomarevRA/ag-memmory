using System.Reflection;
using Xunit;

namespace AgMemory.Web.Tests;

public sealed class MemoryGraphUniverseAssetTests
{
    [Fact]
    public void ThreeJs_IsPinnedVendoredAndLocallyImported()
    {
        var module = Read("src/AgMemory.Web/wwwroot/vendor/three/three.module.js");
        var core = Read("src/AgMemory.Web/wwwroot/vendor/three/three.core.js");
        var license = Read("src/AgMemory.Web/wwwroot/vendor/three/LICENSE");
        var provenance = Read("src/AgMemory.Web/wwwroot/vendor/three/README.md");
        var universe = Read("src/AgMemory.Web/wwwroot/vendor/memory-graph-universe.js");

        Assert.Contains("from './three.core.js'", module, StringComparison.Ordinal);
        Assert.Contains("const REVISION = '185';", core, StringComparison.Ordinal);
        Assert.Contains("MIT License", license, StringComparison.Ordinal);
        Assert.Contains("0.185.1", provenance, StringComparison.Ordinal);
        Assert.Contains("from './three/three.module.js'", universe, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", universe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", universe, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrowserFirebreak_CapsAndSanitizesBeforeEitherRendererReceivesASnapshot()
    {
        var module = Read("src/AgMemory.Web/Features/MemoryGraph/MemoryGraphPage.razor.js");

        Assert.Contains("const MAX_NODES = 75;", module, StringComparison.Ordinal);
        Assert.Contains("const MAX_EDGES = 150;", module, StringComparison.Ordinal);
        Assert.Contains("if (nodes.length === MAX_NODES) break;", module, StringComparison.Ordinal);
        Assert.Contains("if (edges.length === MAX_EDGES) break;", module, StringComparison.Ordinal);
        Assert.Contains("edge.kind !== 'SharedEntity'", module, StringComparison.Ordinal);
        Assert.Contains("edge.sourceId === edge.targetId", module, StringComparison.Ordinal);
        Assert.Contains("currentSnapshot = safeResponse(snapshot);", module, StringComparison.Ordinal);
        Assert.Contains("return safeResponse(await response.json());", module, StringComparison.Ordinal);
        Assert.DoesNotContain("actorId", module, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("memoryId", module, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provenance", module, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LayoutStability_UsesOnlyGenericLocalKeysInsteadOfRenewedOpaqueIds()
    {
        var module = Read("src/AgMemory.Web/Features/MemoryGraph/MemoryGraphPage.razor.js");
        var universe = Read("src/AgMemory.Web/wwwroot/vendor/memory-graph-universe.js");
        var page = Read("src/AgMemory.Web/Features/MemoryGraph/MemoryGraphPage.razor");

        Assert.Contains("const stem = `${node.type}:${node.label}`;", module, StringComparison.Ordinal);
        Assert.Contains("const ordinal = labelOrdinals.get(stem) ?? 0;", module, StringComparison.Ordinal);
        Assert.Contains("node.layoutKey = `${stem}:${ordinal}`;", module, StringComparison.Ordinal);
        Assert.Contains("function sortIds(ids, layoutKeys)", universe, StringComparison.Ordinal);
        Assert.Contains("const layoutKeys = new Map(snapshot.nodes.map(node => [node.id, node.layoutKey", universe, StringComparison.Ordinal);
        Assert.Contains("let labels = new Map(component.map(id => [id, layoutKeys.get(id)]));", universe, StringComparison.Ordinal);
        Assert.Contains("unit(layoutKey, 'radius')", universe, StringComparison.Ordinal);
        Assert.DoesNotContain("unit(id,", universe, StringComparison.Ordinal);
        Assert.DoesNotContain("layoutKey", page, StringComparison.Ordinal);
    }

    [Fact]
    public void RendererModules_AreLocalOnlyAndHaveNoPerpetualAnimationLoop()
    {
        var module = Read("src/AgMemory.Web/Features/MemoryGraph/MemoryGraphPage.razor.js");
        var universe = Read("src/AgMemory.Web/wwwroot/vendor/memory-graph-universe.js");
        var fallback = Read("src/AgMemory.Web/wwwroot/vendor/memory-graph-renderer.js");

        Assert.Contains("fetch(ROUTE", module, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch(", universe, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch(", fallback, StringComparison.Ordinal);
        Assert.DoesNotContain("import(", universe, StringComparison.Ordinal);
        Assert.DoesNotContain("setAnimationLoop", universe, StringComparison.Ordinal);
        Assert.DoesNotContain("autoRotate", universe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Particle", universe, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("if (disposed || raf) return;", universe, StringComparison.Ordinal);
        Assert.Contains("raf = undefined;", universe, StringComparison.Ordinal);
        Assert.Contains("cancelAnimationFrame(raf);", universe, StringComparison.Ordinal);
    }

    [Fact]
    public void WebGlFailureReducedMotionAndDisposal_HaveCompatibleFallbackPaths()
    {
        var module = Read("src/AgMemory.Web/Features/MemoryGraph/MemoryGraphPage.razor.js");
        var universe = Read("src/AgMemory.Web/wwwroot/vendor/memory-graph-universe.js");

        Assert.Contains("function activateFallback()", module, StringComparison.Ordinal);
        Assert.Contains("createMemoryGraphRenderer(fallbackCanvas)", module, StringComparison.Ordinal);
        Assert.Contains("universe?.dispose();", module, StringComparison.Ordinal);
        Assert.Contains("fallbackRenderer?.dispose();", module, StringComparison.Ordinal);
        Assert.Contains("webglcontextlost", universe, StringComparison.Ordinal);
        Assert.Contains("onUnavailable?.('context-lost')", universe, StringComparison.Ordinal);
        Assert.Contains("prefers-reduced-motion: reduce", universe, StringComparison.Ordinal);
        Assert.Contains("renderer.forceContextLoss?.();", universe, StringComparison.Ordinal);
        Assert.Contains("resizeObserver?.disconnect();", universe, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_ProvidesAccessibleThreeDimensionalAndTextSelectionRoutes()
    {
        var page = Read("src/AgMemory.Web/Features/MemoryGraph/MemoryGraphPage.razor");
        var interop = Read("src/AgMemory.Web/Features/MemoryGraph/MemoryGraphInterop.cs");
        var css = Read("src/AgMemory.Web/wwwroot/app.css");

        Assert.Contains("@page \"/memory-graph\"", page, StringComparison.Ordinal);
        Assert.Contains("@ref=\"_fallbackCanvas\"", page, StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"memory-graph-scene-description memory-graph-selection memory-graph-summary\"", page, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\"", page, StringComparison.Ordinal);
        Assert.Contains("@onclick=\"() => SelectNodeAsync(node.Id)\"", page, StringComparison.Ordinal);
        Assert.Contains("if (!_loading && IsAvailable && !_canvasInitialized && _interop is not null)", page, StringComparison.Ordinal);
        Assert.Contains("SharedEntity", page, StringComparison.Ordinal);
        Assert.Contains("не выражают близость, причинность, время или тип отношения", page, StringComparison.Ordinal);
        Assert.Contains("SelectNodeAsync", interop, StringComparison.Ordinal);
        Assert.Contains("SelectNodeMethod = \"selectNode\"", interop, StringComparison.Ordinal);
        Assert.Contains(".memory-graph-stage { grid-template-columns: 1fr; }", css, StringComparison.Ordinal);
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

            throw new InvalidOperationException("Unable to find the repository root for static Web asset verification.");
        }
    }
}
