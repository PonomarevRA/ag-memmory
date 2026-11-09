namespace AgMemory.Web.Features.MemoryExperience;

/// <summary>
/// Server-side switches for memory views that expose operational data.
/// All switches are intentionally disabled until a local developer enables them.
/// </summary>
public sealed class MemoryExperienceOptions
{
    public const string SectionName = "MemoryExperience";

    public bool RecordBrowserEnabled { get; init; }

    public bool RawRecordTextEnabled { get; init; }

    public bool UsageDashboardEnabled { get; init; }
}
