using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ApiGateway.Tests;

/// <summary>
/// The <c>{brand}</c> scope placeholder must resolve from the proxy-set <c>brand</c> header only.
///
/// The brand is decided by the real domain the request arrived on, which the edge proxy turns into the trusted
/// <c>brand</c> header. A caller-controlled header (the old gateway preferred <c>X-Brand</c>) would let a caller pick
/// the brand its own <c>{brand}</c> predicate is checked against — so it must never be consulted.
/// </summary>
public class BrandHeaderTests
{
    private sealed class NoHttpClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static readonly IConfiguration Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["EndPoints:svc"] = "http://svc:8080/",
        ["proxyConfig:0:ProxyName"] = "svc",
        ["proxyConfig:0:ApiUrl"] = "v1/bet",
        ["proxyConfig:0:ProxyUrl"] = "v1/bet",
        ["proxyConfig:0:AnyScopesPredicate:0"] = "bet-w:b={brand}"
    }).Build();

    private static readonly ProxyConfig BrandScopedRoute = HttpMessageLogic.LoadProxyConfig(Config)[0];

    private static async Task<bool> Authorize(string scopes, params (string Header, string Value)[] headers)
    {
        var options = Options.Create(new GatewayOptions());
        var proxy = new Proxy(Config, new NoHttpClients());
        var logic = new HttpMessageLogic(proxy, NullLoggerFactory.Instance,
            new RouteTableProvider(Config, options, NullLoggerFactory.Instance), options);

        var request = new DefaultHttpContext().Request;
        foreach (var (header, value) in headers)
            request.Headers[header] = value;

        return await logic.IsAuthValid(scopes, BrandScopedRoute, "user-1", proxy, request,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_trusted_brand_header_is_what_the_predicate_is_checked_against()
        => Assert.True(await Authorize("bet-w:b=acme", ("brand", "acme")));

    [Fact]
    public async Task A_scope_for_a_different_brand_is_rejected()
        => Assert.False(await Authorize("bet-w:b=other", ("brand", "acme")));

    [Fact]
    public async Task X_Brand_cannot_override_the_trusted_brand()
        // The caller holds a scope for "acme" and claims to be "acme" via X-Brand, but the domain says "other".
        => Assert.False(await Authorize("bet-w:b=acme", ("brand", "other"), ("X-Brand", "acme")));

    [Fact]
    public async Task X_Brand_does_not_block_a_legitimate_request()
        // Sending a junk X-Brand must not break a caller whose trusted brand matches their scope.
        => Assert.True(await Authorize("bet-w:b=acme", ("brand", "acme"), ("X-Brand", "somewhere-else")));

    [Fact]
    public async Task X_Brand_is_not_a_fallback_when_the_trusted_brand_is_missing()
        => Assert.False(await Authorize("bet-w:b=acme", ("X-Brand", "acme")));
}
