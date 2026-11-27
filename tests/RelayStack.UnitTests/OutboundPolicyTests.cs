using System.Net;
using RelayStack.Platform.Security;

namespace RelayStack.UnitTests;

public class OutboundPolicyTests
{
    [Theory]
    [InlineData("8.8.8.8", AddressClass.Public)]
    [InlineData("93.184.216.34", AddressClass.Public)]
    [InlineData("127.0.0.1", AddressClass.Private)]
    [InlineData("127.255.255.254", AddressClass.Private)]
    [InlineData("10.1.2.3", AddressClass.Private)]
    [InlineData("172.16.0.1", AddressClass.Private)]
    [InlineData("172.31.255.255", AddressClass.Private)]
    [InlineData("172.32.0.1", AddressClass.Public)]
    [InlineData("192.168.1.1", AddressClass.Private)]
    [InlineData("100.64.0.1", AddressClass.Private)]
    [InlineData("100.100.100.200", AddressClass.Private)]
    [InlineData("169.254.169.254", AddressClass.AlwaysBlocked)]
    [InlineData("169.254.0.1", AddressClass.AlwaysBlocked)]
    [InlineData("169.254.170.2", AddressClass.AlwaysBlocked)]                 // ECS task credentials endpoint
    [InlineData("0.0.0.0", AddressClass.AlwaysBlocked)]
    [InlineData("224.0.0.1", AddressClass.AlwaysBlocked)]
    [InlineData("255.255.255.255", AddressClass.AlwaysBlocked)]
    [InlineData("198.18.0.1", AddressClass.Private)]
    [InlineData("::1", AddressClass.Private)]
    [InlineData("::", AddressClass.AlwaysBlocked)]
    [InlineData("fe80::1", AddressClass.AlwaysBlocked)]
    [InlineData("fd00:ec2::254", AddressClass.AlwaysBlocked)]
    [InlineData("fd12:3456::1", AddressClass.Private)]
    [InlineData("ff02::1", AddressClass.AlwaysBlocked)]
    [InlineData("2606:4700:4700::1111", AddressClass.Public)]
    [InlineData("::ffff:127.0.0.1", AddressClass.Private)]
    [InlineData("::ffff:169.254.169.254", AddressClass.AlwaysBlocked)]
    [InlineData("::ffff:8.8.8.8", AddressClass.Public)]
    [InlineData("64:ff9b::a9fe:a9fe", AddressClass.AlwaysBlocked)] // NAT64 of 169.254.169.254
    [InlineData("64:ff9b::808:808", AddressClass.Public)]
    [InlineData("2002:7f00:1::", AddressClass.Private)]            // 6to4 of 127.0.0.1
    [InlineData("2001:db8::1", AddressClass.Private)]
    [InlineData("::127.0.0.1", AddressClass.AlwaysBlocked)]        // deprecated IPv4-compatible
    public void Classifies_addresses(string ip, AddressClass expected) =>
        Assert.Equal(expected, AddressClassifier.Classify(IPAddress.Parse(ip)));

    [Fact]
    public void Production_policy_blocks_private_addresses_even_for_allowlisted_names()
    {
        var policy = OutboundPolicy.Production;
        Assert.Throws<OutboundRequestBlockedException>(() => policy.EnsureAllowed(IPAddress.Loopback, "dev-receiver", 8080, requestAllowsDevReceiver: true));
        policy.EnsureAllowed(IPAddress.Parse("8.8.8.8"), "example.com", 443, requestAllowsDevReceiver: false);
    }

    [Fact]
    public void Development_allowlist_cannot_be_configured_outside_development()
    {
        Assert.Throws<InvalidOperationException>(() => new OutboundPolicy(["dev-receiver:8080"], isDevelopmentHost: false));
    }

    [Fact]
    public void Development_exception_requires_exact_host_port_and_a_development_request()
    {
        var policy = new OutboundPolicy(["dev-receiver:8080"], isDevelopmentHost: true);
        var privateIp = IPAddress.Parse("172.18.0.5");
        policy.EnsureAllowed(privateIp, "dev-receiver", 8080, requestAllowsDevReceiver: true);
        policy.EnsureAllowed(privateIp, "DEV-RECEIVER", 8080, requestAllowsDevReceiver: true);
        Assert.Throws<OutboundRequestBlockedException>(() => policy.EnsureAllowed(privateIp, "dev-receiver", 8081, true));
        Assert.Throws<OutboundRequestBlockedException>(() => policy.EnsureAllowed(privateIp, "other", 8080, true));
        Assert.Throws<OutboundRequestBlockedException>(() => policy.EnsureAllowed(privateIp, "dev-receiver", 8080, requestAllowsDevReceiver: false));
    }

    [Fact]
    public void Development_exception_never_allows_metadata_or_link_local()
    {
        var policy = new OutboundPolicy(["dev-receiver:8080"], isDevelopmentHost: true);
        Assert.Throws<OutboundRequestBlockedException>(() => policy.EnsureAllowed(IPAddress.Parse("169.254.169.254"), "dev-receiver", 8080, true));
        Assert.Throws<OutboundRequestBlockedException>(() => policy.EnsureAllowed(IPAddress.Parse("fd00:ec2::254"), "dev-receiver", 8080, true));
    }

    [Theory]
    [InlineData("https://hooks.example.com/relaystack", true, null)]
    [InlineData("https://hooks.example.com:8443/x", true, null)]
    [InlineData("http://hooks.example.com/x", true, "url must use https.")]
    [InlineData("https://user:pw@hooks.example.com/x", true, "url must not contain credentials.")]
    [InlineData("https://hooks.example.com:22/x", true, "url port must be 443 or 8443.")]
    [InlineData("https://hooks.example.com:9443/x", true, "url port must be 443 or 8443.")]
    [InlineData("https://localhost/x", true, "url must not point at a loopback host.")]
    [InlineData("https://169.254.169.254/latest/meta-data", true, "url must not point at a private, loopback, link-local or metadata address.")]
    [InlineData("https://[::1]/x", true, "url must not point at a loopback host.")]
    [InlineData("http://dev-receiver:8080/webhooks", true, null)]
    [InlineData("http://dev-receiver:8080/webhooks", false, "url must use https.")]
    [InlineData("not a url", true, "url must be an absolute URL of at most 2048 characters.")]
    public void Validates_destination_urls(string url, bool devEnvironment, string? expectedError)
    {
        var policy = new OutboundPolicy(["dev-receiver:8080"], isDevelopmentHost: true);
        Assert.Equal(expectedError, policy.ValidateDestinationUrl(url, devEnvironment));
    }

    [Fact]
    public async Task Safe_client_blocks_names_that_resolve_to_private_addresses()
    {
        // Simulates DNS rebinding / a hostile DNS answer: the name looks public but resolves to metadata.
        using var client = SafeHttpClientFactory.Create(OutboundPolicy.Production, TimeSpan.FromSeconds(2),
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("169.254.169.254") }));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://totally-public.example.com/"));
        Assert.IsType<OutboundRequestBlockedException>(ex.InnerException);
    }

    [Fact]
    public async Task Safe_client_blocks_mixed_public_and_private_answers()
    {
        using var client = SafeHttpClientFactory.Create(OutboundPolicy.Production, TimeSpan.FromSeconds(2),
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("10.0.0.5") }));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://mixed.example.com/"));
        Assert.IsType<OutboundRequestBlockedException>(ex.InnerException);
    }

    [Fact]
    public async Task Safe_client_blocks_ip_literals()
    {
        using var client = SafeHttpClientFactory.Create(OutboundPolicy.Production, TimeSpan.FromSeconds(2));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://127.0.0.1:9/"));
        Assert.IsType<OutboundRequestBlockedException>(ex.InnerException);
    }
}
