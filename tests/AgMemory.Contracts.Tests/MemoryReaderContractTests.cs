using System.Reflection;
using AgMemory.Contracts;
using Xunit;

namespace AgMemory.Contracts.Tests;

public sealed class MemoryReaderContractTests
{
    [Fact]
    public void ReaderRead_UsesDedicatedQueryAndSourcePorts()
    {
        Assert.Contains(MemoryOperation.ReaderRead, Enum.GetValues<MemoryOperation>());
        AssertParameter(typeof(IMemoryReaderQueryService), nameof(IMemoryReaderQueryService.ReadHomeAsync), typeof(MemoryReaderHomeRequest));
        AssertParameter(typeof(IMemoryReaderQueryService), nameof(IMemoryReaderQueryService.ReadDocumentAsync), typeof(MemoryReaderDocumentRequest));
        AssertParameter(typeof(IMemoryReaderSource), nameof(IMemoryReaderSource.ReadByIdAsync), typeof(MemorySearchEligibility));
        Assert.DoesNotContain(typeof(IMemoryReaderQueryService).GetMethods(), method => method.Name.Contains("Index", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(IMemoryReaderSource).GetMethods(), method => method.Name.Contains("Page", StringComparison.Ordinal));
    }

    [Fact]
    public void ReaderLimits_AreFixedAndBounded()
    {
        Assert.Equal(8, MemoryReaderLimits.BlocksPerPage);
        Assert.Equal(256, MemoryReaderLimits.MaximumBlocksPerDocument);
        Assert.Equal(64, MemoryReaderLimits.MaximumLinksPerBlock);
        Assert.Equal(8_000, MemoryReaderLimits.MaximumBlockCharacters);
        Assert.Equal(120_000, MemoryReaderLimits.MaximumDocumentCharacters);
    }

    [Fact]
    public void ReaderRequests_KeepAuthorityServerSideAndDoNotAcceptPageLimits()
    {
        AssertParameter(typeof(MemoryReaderHomeRequest), nameof(MemoryReaderHomeRequest.Actor), typeof(ActorId));
        AssertParameter(typeof(MemoryReaderHomeRequest), nameof(MemoryReaderHomeRequest.RequestedScope), typeof(MemoryScope));
        AssertParameter(typeof(MemoryReaderHomeRequest), nameof(MemoryReaderHomeRequest.HomeMemoryId), typeof(MemoryId));
        AssertParameter(typeof(MemoryReaderDocumentRequest), nameof(MemoryReaderDocumentRequest.Actor), typeof(ActorId));
        AssertParameter(typeof(MemoryReaderDocumentRequest), nameof(MemoryReaderDocumentRequest.RequestedScope), typeof(MemoryScope));

        AssertNoPageLimit(typeof(MemoryReaderHomeRequest));
        AssertNoPageLimit(typeof(MemoryReaderDocumentRequest));
    }

    [Fact]
    public void ReaderPageModels_KeepAuthorityAndDurableIdentifiersOutOfRenderedBlocks()
    {
        var renderedModels = new[]
        {
            typeof(MemoryReaderBlock),
            typeof(MemoryReaderInline)
        };

        var prohibitedTypes = new[]
        {
            typeof(ActorId),
            typeof(MemoryScope),
            typeof(MemoryId),
            typeof(MemoryReaderBlockCursor),
            typeof(MemoryReaderSourceRecord)
        };

        foreach (var model in renderedModels)
        {
            var properties = model.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            Assert.DoesNotContain(properties, property => prohibitedTypes.Contains(property.PropertyType));
            Assert.DoesNotContain(properties, property => property.Name.Contains("Limit", StringComparison.Ordinal));
        }

        Assert.Equal(typeof(MemoryReaderBlockCursor),
            typeof(MemoryReaderDocumentPage).GetProperty(nameof(MemoryReaderDocumentPage.NextCursor))!.PropertyType);
    }

    [Fact]
    public void ReaderSourceProjection_HasNoEmbeddingOrGeneralMemoryRecordPayload()
    {
        var properties = typeof(MemoryReaderSourceRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.DoesNotContain(properties, property => property.PropertyType == typeof(MemoryRecord));
        Assert.DoesNotContain(properties, property => property.PropertyType == typeof(EmbeddingReference));
        Assert.DoesNotContain(properties, property => property.PropertyType == typeof(ReadOnlyMemory<float>));
        Assert.DoesNotContain(properties, property => property.PropertyType == typeof(ReadOnlyMemory<float>?));
        Assert.DoesNotContain(properties, property => property.Name.Contains("Embedding", StringComparison.Ordinal));
    }

    [Fact]
    public void ReaderContracts_HaveNoAgmDependency()
    {
        var assembly = typeof(MemoryReaderLimits).Assembly;
        var readerTypes = new[]
        {
            typeof(IMemoryReaderQueryService),
            typeof(IMemoryReaderSource),
            typeof(MemoryReaderLimits),
            typeof(MemoryReaderHomeRequest),
            typeof(MemoryReaderDocumentRequest),
            typeof(MemoryReaderDocumentPage)
        };

        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name?.StartsWith("Agm.", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(readerTypes, type =>
            type.Namespace?.StartsWith("Agm.", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static void AssertParameter(Type type, string memberName, Type expectedType)
    {
        var property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
        if (property is not null)
        {
            Assert.Equal(expectedType, property.PropertyType);
            return;
        }

        var method = Assert.Single(type.GetMethods(BindingFlags.Public | BindingFlags.Instance), item => item.Name == memberName);
        Assert.Contains(method.GetParameters(), parameter => parameter.ParameterType == expectedType);
    }

    private static void AssertNoPageLimit(Type requestType) =>
        Assert.DoesNotContain(requestType.GetProperties(BindingFlags.Public | BindingFlags.Instance), property =>
            property.Name.Contains("Limit", StringComparison.Ordinal) ||
            property.Name.Contains("PageSize", StringComparison.Ordinal));
}
