namespace AgMemory.Contracts;

/// <summary>
/// Identifies a public memory operation for authorisation and telemetry policies.
/// </summary>
public enum MemoryOperation
{
    Remember,
    Lifecycle,
    AppendHotMemory,
    Forget,
    Search,
    BuildContext,
    ReadHotMemory,
    GraphRead,
    ReaderRead
}
