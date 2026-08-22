using Shouldly;
using Trinetra.Federation.Adapters.Onvif;

namespace Trinetra.UnitTests;

/// <summary>
/// Covers the single most common ONVIF integration bug: devices returning subscription and
/// service addresses built from their own internal view of the network.
/// </summary>
public sealed class OnvifAddressRewriteTests
{
    [Fact]
    public void Rewrite_ReplacesNattedHostWithTheConfiguredEndpoint()
    {
        // The device believes it is 192.168.1.64, but we reach it through a departmental
        // router. Using its address verbatim sends every pull to an unroutable host.
        var result = OnvifAdapter.RewriteSubscriptionAddress(
            "http://192.168.1.64/onvif/Subscription?Idx=3",
            "http://10.20.30.40:8000");

        result.Host.ShouldBe("10.20.30.40");
        result.Port.ShouldBe(8000);
        result.PathAndQuery.ShouldBe("/onvif/Subscription?Idx=3");
    }

    [Fact]
    public void Rewrite_PreservesThePathAndQueryExactly()
    {
        // Only the authority is wrong. The path and its reference parameters identify the
        // subscription and must survive untouched.
        var result = OnvifAdapter.RewriteSubscriptionAddress(
            "http://192.168.1.64:80/onvif/Subscription?Idx=7&sub=abc",
            "https://cam.dept.gov.in");

        result.PathAndQuery.ShouldBe("/onvif/Subscription?Idx=7&sub=abc");
        result.Scheme.ShouldBe("https");
    }

    [Fact]
    public void Rewrite_HandlesRelativeAddresses()
    {
        var result = OnvifAdapter.RewriteSubscriptionAddress(
            "/onvif/Subscription?Idx=1", "http://10.0.0.5:8080");

        result.ShouldBe(new Uri("http://10.0.0.5:8080/onvif/Subscription?Idx=1"));
    }

    [Fact]
    public void Rewrite_KeepsTheEndpointWhenTheDeviceAlreadyAgrees()
    {
        var result = OnvifAdapter.RewriteSubscriptionAddress(
            "http://10.0.0.5:8080/onvif/Subscription", "http://10.0.0.5:8080");

        result.ShouldBe(new Uri("http://10.0.0.5:8080/onvif/Subscription"));
    }

    [Theory]
    [InlineData("10.0.0.5", "http://10.0.0.5/onvif/device_service")]
    [InlineData("http://10.0.0.5", "http://10.0.0.5/onvif/device_service")]
    [InlineData("http://10.0.0.5:8080", "http://10.0.0.5:8080/onvif/device_service")]
    [InlineData("http://10.0.0.5/custom/path", "http://10.0.0.5/custom/path")]
    public void BuildDeviceServiceUri_AppendsConventionalPathOnlyWhenNoneGiven(
        string endpoint, string expected) =>
        OnvifAdapter.BuildDeviceServiceUri(endpoint).ToString().TrimEnd('/')
            .ShouldBe(expected.TrimEnd('/'));
}
