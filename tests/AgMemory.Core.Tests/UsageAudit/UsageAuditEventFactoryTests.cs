using AgMemory.Contracts;
using AgMemory.Core;
using Xunit;

namespace AgMemory.Core.Tests.UsageAudit;

public sealed class UsageAuditEventFactoryTests
{
    private readonly UsageAuditEventFactory _factory = new(Enumerable.Range(0, 32).Select(index => (byte)index).ToArray());

    [Fact]
    public void DescribeQuery_UsesHmacAndLengthWithoutRetainingQueryText()
    {
        const string query = "customer secret request";

        var descriptor = _factory.DescribeQuery(query);

        Assert.Equal(query.Length, descriptor.LengthChars);
        Assert.NotEqual(query, descriptor.Hash);
        Assert.Matches("^[0-9a-f]{64}$", descriptor.Hash);
        Assert.Equal(descriptor, _factory.DescribeQuery(query));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(3204, 800)]
    public void ContextBuild_EstimatesOnlySuccessfulDeliveredContext(int selectedChars, int expectedTokens)
    {
        var succeeded = _factory.Create(Guid.NewGuid(), DateTimeOffset.UnixEpoch, UsageAuditSource.Mcp,
            UsageAuditOperation.ContextBuild, UsageAuditClientLabel.Codex, "default", "query", 2, selectedChars, UsageAuditOutcome.Succeeded);
        var failed = _factory.Create(Guid.NewGuid(), DateTimeOffset.UnixEpoch, UsageAuditSource.Mcp,
            UsageAuditOperation.ContextBuild, UsageAuditClientLabel.Codex, "default", "query", 0, selectedChars,
            UsageAuditOutcome.Failed, UsageAuditFailureClass.Timeout);

        Assert.Equal(expectedTokens, succeeded.DeliveredTokensEstimate);
        Assert.Equal(0, failed.DeliveredTokensEstimate);
        Assert.Equal(UsageAuditPolicy.EstimationMethodVersion, succeeded.EstimationMethodVersion);
    }

    [Fact]
    public void Event_RejectsUnsafeAreaAndSuccessFailureClass()
    {
        Assert.Throws<ArgumentException>(() => _factory.Create(Guid.NewGuid(), DateTimeOffset.UnixEpoch, UsageAuditSource.LocalChat,
            UsageAuditOperation.Recall, UsageAuditClientLabel.LocalChat, "raw scope/value", "query", 0, 0, UsageAuditOutcome.Succeeded));
        Assert.Throws<ArgumentException>(() => _factory.Create(Guid.NewGuid(), DateTimeOffset.UnixEpoch, UsageAuditSource.LocalChat,
            UsageAuditOperation.Recall, UsageAuditClientLabel.LocalChat, "default", "query", 0, 0,
            UsageAuditOutcome.Succeeded, UsageAuditFailureClass.Storage));
    }
}
