using System.Net;

namespace AgMemory.Web.Features.MemoryExperience;

/// <summary>Keeps future operational memory views local to a Development host.</summary>
public static class MemoryExperienceAccessPolicy
{
    public static bool Allows(bool flagEnabled, bool isDevelopment, IPAddress? remoteAddress) =>
        flagEnabled && isDevelopment && remoteAddress is not null && IPAddress.IsLoopback(remoteAddress);

    /// <summary>Raw text is a refinement of the record browser, never a standalone surface.</summary>
    public static bool AllowsRawRecordText(
        MemoryExperienceOptions options,
        bool isDevelopment,
        IPAddress? remoteAddress) =>
        Allows(options.RecordBrowserEnabled && options.RawRecordTextEnabled, isDevelopment, remoteAddress);
}
