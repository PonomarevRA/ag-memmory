using System.Runtime.CompilerServices;

// Fault hooks remain internal and are used only by isolated verification executables/tests.
[assembly: InternalsVisibleTo("AgMemory.KnowledgeFaultProbe")]
[assembly: InternalsVisibleTo("AgMemory.McpServer.Tests")]
