namespace AgMemory.Web.Tests;

/// <summary>Stable paths to Vite client sources checked by browser contract tests.</summary>
internal static class FrontendSourcePaths
{
    private static string Root => RepositoryRoot;

    public static string Chat => Path.Combine(Root, "src/AgMemory.Web/client/src/features/chat/chat-stream.ts");
    public static string MemoryReader => Path.Combine(Root, "src/AgMemory.Web/client/src/features/memory-reader");
    public static string MemoryReaderSanitize => Path.Combine(MemoryReader, "sanitize.ts");
    public static string MemoryReaderApi => Path.Combine(MemoryReader, "api.ts");
    public static string MemoryGraphSanitize => Path.Combine(Root, "src/AgMemory.Web/client/src/features/memory-graph/sanitize.ts");
    public static string MemoryGraphController => Path.Combine(Root, "src/AgMemory.Web/client/src/features/memory-graph/page-controller.ts");
    public static string MemoryStatus => Path.Combine(Root, "src/AgMemory.Web/client/src/features/memory-status/api.ts");
    public static string Navigation => Path.Combine(Root, "src/AgMemory.Web/client/src/features/navigation/browser-state.ts");
    public static string ReconnectModal => Path.Combine(Root, "src/AgMemory.Web/client/src/layout/reconnect-modal.ts");
    public static string SharedFetch => Path.Combine(Root, "src/AgMemory.Web/client/src/shared/fetch.ts");

    public static string Read(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

    public static string ReadAbsolute(string absolutePath) => File.ReadAllText(absolutePath);

    private static string RepositoryRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "ag-memory.slnx"))) return current.FullName;
                current = current.Parent;
            }

            throw new InvalidOperationException("Unable to find repository root for frontend contract tests.");
        }
    }
}
