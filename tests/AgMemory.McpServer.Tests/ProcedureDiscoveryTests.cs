using Xunit;

namespace AgMemory.McpServer.Tests;

/// <summary>Controlled discovery qualification; these cases test declared metadata, not execution success or cross-model reader benefit.</summary>
public sealed class ProcedureDiscoveryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "agmemory-procedure-discovery-" + Guid.NewGuid().ToString("N"));
    private LocalAgMemoryMcpConfiguration Config => LocalAgMemoryMcpConfiguration.Create(_path, "procedure-reader", "procedure-tenant");

    /// <summary>25 procedure/environment pairs cover exact labels, family transfer, missing capabilities and unrelated families.</summary>
    [Fact]
    public async Task TwentyFiveDeclaredTransferPairsRespectEligibilityAndReview()
    {
        await using var runtime = new LocalAgMemoryMcpRuntime(Config);
        for (var index = 0; index < 25; index++)
        {
            var created = await runtime.KnowledgeProcedureAsync(null, 0, "Export diagnostics fixture" + index,
                ["выгрузка диагностики"], "environment-" + index, "Read authorization", "Export a bounded interval", "Check archive", null, "procedure_" + index);
            Assert.Null(created.Error);
            var facets = await runtime.KnowledgeProcedureFacetsAsync(created.Data!.ProcedureRef, "family_" + index,
                ["read_logs"], ["Authorization must be confirmed"], ["Different tools need a reviewed command mapping"], "facets_" + index);
            Assert.Null(facets.Error);
            var exact = index < 8;
            var missing = index is >= 16 and < 21;
            var unrelated = index >= 21;
            var results = await runtime.KnowledgeProcedureDiscoveryAsync("fixture" + index + " diagnostics",
                exact ? "environment-" + index : "target-" + index, missing ? [] : ["read_logs"],
                unrelated ? "unrelated" : "family_" + index);
            Assert.Null(results.Error);
            if (missing || unrelated) { Assert.Empty(results.Data!); continue; }
            var item = Assert.Single(results.Data!);
            Assert.Equal(created.Data.Revision, item.Procedure.Revision);
            Assert.Equal(exact ? "ExactRevisionNeedsPreconditionCheck" : "TransferCandidateNeedsReview", item.ApplicabilityStatus);
            Assert.Equal("caller_reported_unverified", item.EvidenceTrust);
            Assert.NotEmpty(item.Preconditions);
            Assert.NotEmpty(item.TransferWarnings);
        }
    }

    /// <summary>Legacy discovery remains exact/substring based; new revisions lose stale facets and cannot inherit transfer qualification.</summary>
    [Fact]
    public async Task FacetsAreImmutableRevisionBoundAndLegacyIsUnchanged()
    {
        await using var runtime = new LocalAgMemoryMcpRuntime(Config);
        var created = (await runtime.KnowledgeProcedureAsync(null, 0, "Backup verification", ["проверка резервной копии"], "old-env",
            "Read access", "Check checksum", "Checksum matches", null, "create")).Data!;
        Assert.Null((await runtime.KnowledgeProcedureFacetsAsync(created.ProcedureRef, "backup-family", ["read_backup"], [], [], "facets")).Error);
        Assert.Equal("request_conflict", (await runtime.KnowledgeProcedureFacetsAsync(created.ProcedureRef, "different-family", ["read_backup"], [], [], "changed")).Error!.Code);
        Assert.Empty((await runtime.KnowledgeProceduresAsync("backup", "target-env")).Data!);
        Assert.Single((await runtime.KnowledgeProcedureDiscoveryAsync("резервную копию", "target-env", ["read_backup"], "backup-family")).Data!);
        var updated = await runtime.KnowledgeProcedureAsync(created.ProcedureRef, 1, "Backup verification", ["проверка резервной копии"], "old-env",
            "New access", "New checksum step", "New verification", null, "update");
        Assert.Null(updated.Error);
        Assert.Empty((await runtime.KnowledgeProcedureDiscoveryAsync("backup", "target-env", ["read_backup"], "backup-family")).Data!);
        Assert.Equal("ExactLabelOnlyNeedsReview", Assert.Single((await runtime.KnowledgeProcedureDiscoveryAsync("backup", "old-env", [])).Data!).ApplicabilityStatus);
    }

    public void Dispose() { if (Directory.Exists(_path)) Directory.Delete(_path, true); }
}
