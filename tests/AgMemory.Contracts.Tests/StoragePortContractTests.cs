using System.Reflection;
using AgMemory.Contracts;
using AgMemory.Core;
using Xunit;

namespace AgMemory.Contracts.Tests;

public sealed class StoragePortContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StorageOperations_RequireAuthorizedSelectors_AndOutboxIsScopeBound()
    {
        AssertParameter(typeof(IMemoryStore), nameof(IMemoryStore.GetAsync), typeof(AuthorizedScopeSet));
        AssertParameter(typeof(IMemoryStore), nameof(IMemoryStore.ListAsync), typeof(AuthorizedScopeSet));
        AssertParameter(typeof(IMemoryStore), nameof(IMemoryStore.GetHotMemoryAsync), typeof(AuthorizedScopeSet));

        var transactionOperations = typeof(IMemoryStoreTransaction)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.Name != nameof(IMemoryStoreTransaction.CommitAsync));
        Assert.All(transactionOperations, operation => Assert.Contains(
            operation.GetParameters(), parameter => parameter.ParameterType == typeof(AuthorizedScopeSet)));

        Assert.Equal(typeof(ScopeSelector), typeof(OutboxMessage).GetProperty(nameof(OutboxMessage.Scope))!.PropertyType);
    }

    [Fact]
    public void RetrievalAndGraphPorts_ReceiveExplicitPreRankingEligibility()
    {
        AssertParameter(typeof(IVectorSearch), nameof(IVectorSearch.SearchAsync), typeof(SearchPortRequest));
        AssertParameter(typeof(ILexicalSearch), nameof(ILexicalSearch.SearchAsync), typeof(SearchPortRequest));
        AssertParameter(typeof(IMemoryGraph), nameof(IMemoryGraph.RerankAsync), typeof(GraphRerankRequest));

        Assert.Equal(typeof(MemorySearchEligibility),
            typeof(SearchPortRequest).GetProperty(nameof(SearchPortRequest.Eligibility))!.PropertyType);
        Assert.Equal(typeof(ReadOnlyMemory<float>?),
            typeof(SearchPortRequest).GetProperty(nameof(SearchPortRequest.QueryVector))!.PropertyType);
        Assert.Equal(typeof(MemorySearchEligibility),
            typeof(GraphRerankRequest).GetProperty(nameof(GraphRerankRequest.Eligibility))!.PropertyType);

        var scope = new MemoryScope(new("tenant-a"), new("project-a"), new("workspace-a"), new("chat-a"), new("run-a"));
        var otherRun = scope with { RunId = new ScopeId("run-b") };
        var eligibility = new MemorySearchEligibility(new AuthorizedScopeSet([new ScopeSelector(scope)]),
            new HashSet<MemoryRecordType> { MemoryRecordType.Fact }, Now);

        Assert.True(eligibility.ExcludesExpiredRecords);
        Assert.Equal(MemoryLifecycleStatus.Active, eligibility.RequiredLifecycleStatus);
        Assert.True(eligibility.IsEligible(Record("allowed", scope)));
        Assert.False(eligibility.IsEligible(Record("wrong-run", otherRun)));
        Assert.False(eligibility.IsEligible(Record("inactive", scope, status: MemoryLifecycleStatus.Invalid)));
        Assert.False(eligibility.IsEligible(Record("expired", scope, expiresAt: Now)));
        Assert.False(eligibility.IsEligible(Record("wrong-type", scope, type: MemoryRecordType.Summary)));
    }

    [Fact]
    public void ContractsAndCore_PublicApiAndReferences_DoNotExposeProhibitedDependencies()
    {
        var prohibitedTokens = new[] { "Lance", "Arrow", "Sqlite", "Microsoft.AspNetCore", "Agm.", "IServiceProvider" };
        var assemblies = new[] { typeof(MemoryScope).Assembly, typeof(MemoryQueryService).Assembly };

        foreach (var assembly in assemblies)
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => IsProhibited(reference.Name, prohibitedTokens));

            foreach (var exportedType in assembly.GetExportedTypes())
            {
                foreach (var reachableType in PublicApiTypes(exportedType))
                    Assert.False(IsProhibited(reachableType, prohibitedTokens),
                        $"Public API leak in {assembly.GetName().Name}: {exportedType.FullName} exposes {reachableType.FullName ?? reachableType.Name}.");
            }
        }
    }

    private static void AssertParameter(Type port, string methodName, Type expectedType)
    {
        var method = Assert.Single(port.GetMethods(), item => item.Name == methodName);
        Assert.Contains(method.GetParameters(), parameter => parameter.ParameterType == expectedType);
    }

    private static MemoryRecord Record(
        string id,
        MemoryScope scope,
        MemoryRecordType type = MemoryRecordType.Fact,
        MemoryLifecycleStatus status = MemoryLifecycleStatus.Active,
        DateTimeOffset? expiresAt = null) => new(
        new MemoryId(id), scope, type, status, "provider-neutral record", null, .5, .5, 5,
        Now, Now, 1, ["entity"], new MemoryProvenance("test", null, null, null, null, null, null, [new("evidence")]),
        null, expiresAt, $"dedup-{id}");

    private static IEnumerable<Type> PublicApiTypes(Type exportedType)
    {
        var seen = new HashSet<Type>();
        foreach (var candidate in Expand(exportedType, seen)) yield return candidate;

        const BindingFlags declaredPublic = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var implementedInterface in exportedType.GetInterfaces())
            foreach (var candidate in Expand(implementedInterface, seen)) yield return candidate;
        foreach (var constructor in exportedType.GetConstructors(declaredPublic))
            foreach (var parameter in constructor.GetParameters())
                foreach (var candidate in Expand(parameter.ParameterType, seen)) yield return candidate;
        foreach (var method in exportedType.GetMethods(declaredPublic))
        {
            foreach (var candidate in Expand(method.ReturnType, seen)) yield return candidate;
            foreach (var parameter in method.GetParameters())
                foreach (var candidate in Expand(parameter.ParameterType, seen)) yield return candidate;
        }
        foreach (var property in exportedType.GetProperties(declaredPublic))
            foreach (var candidate in Expand(property.PropertyType, seen)) yield return candidate;
        foreach (var field in exportedType.GetFields(declaredPublic))
            foreach (var candidate in Expand(field.FieldType, seen)) yield return candidate;
        foreach (var @event in exportedType.GetEvents(declaredPublic))
            foreach (var candidate in Expand(@event.EventHandlerType!, seen)) yield return candidate;
    }

    private static IEnumerable<Type> Expand(Type type, ISet<Type> seen)
    {
        if (!seen.Add(type)) yield break;
        yield return type;

        if (type.HasElementType)
            foreach (var candidate in Expand(type.GetElementType()!, seen)) yield return candidate;
        if (type.IsGenericType)
            foreach (var genericArgument in type.GetGenericArguments())
                foreach (var candidate in Expand(genericArgument, seen)) yield return candidate;
        if (type.IsGenericParameter)
            foreach (var constraint in type.GetGenericParameterConstraints())
                foreach (var candidate in Expand(constraint, seen)) yield return candidate;
    }

    private static bool IsProhibited(Type type, IEnumerable<string> tokens) =>
        IsProhibited(type.FullName, tokens) ||
        IsProhibited(type.Namespace, tokens) ||
        IsProhibited(type.Assembly.GetName().Name, tokens);

    private static bool IsProhibited(string? value, IEnumerable<string> tokens) =>
        value is not null && tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));
}
