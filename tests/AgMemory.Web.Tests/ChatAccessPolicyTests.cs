using System.Net;
using AgMemory.Web.Features.Chat;
using AgMemory.Web.Gateway;
using Xunit;

namespace AgMemory.Web.Tests;

public sealed class ChatAccessPolicyTests
{
    [Fact]
    public void Disabled_gateway_does_not_turn_an_unavailable_state_into_an_access_denial()
    {
        var allowed = ChatAccessPolicy.AllowsGatewayInvocation(
            new(false, false, "Модель не настроена"), false, IPAddress.Parse("203.0.113.15"));

        Assert.True(allowed);
    }

    [Fact]
    public void Configured_gateway_is_allowed_from_loopback_only_during_development()
    {
        var allowed = ChatAccessPolicy.AllowsGatewayInvocation(
            new(true, false, "Модель подключена"), true, IPAddress.Loopback);

        Assert.True(allowed);
    }

    [Fact]
    public void Configured_gateway_is_rejected_in_production_before_a_provider_can_be_called()
    {
        var allowed = ChatAccessPolicy.AllowsGatewayInvocation(
            new(true, false, "Модель подключена"), false, IPAddress.Loopback);

        Assert.False(allowed);
    }

    [Fact]
    public void Configured_gateway_is_rejected_for_non_loopback_development_requests()
    {
        var allowed = ChatAccessPolicy.AllowsGatewayInvocation(
            new(true, false, "Модель подключена"), true, IPAddress.Parse("203.0.113.15"));

        Assert.False(allowed);
    }
}
