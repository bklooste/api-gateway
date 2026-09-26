using Microsoft.Extensions.Configuration;

namespace ApiGateway.Tests;

/// <summary>Binding <c>proxyConfig</c> and folding <c>globalRequiredScopes</c> into every route.</summary>
public class ProxyConfigTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void LoadProxyConfig_binds_routes()
    {
        var config = Config(new()
        {
            ["proxyConfig:0:ProxyName"] = "offer_events",
            ["proxyConfig:0:ApiUrl"] = "v2/event",
            ["proxyConfig:0:ProxyUrl"] = "v1/event",
            ["proxyConfig:0:AnyScopesPredicate:0"] = "events-r"
        });

        var routes = HttpMessageLogic.LoadProxyConfig(config);

        var route = Assert.Single(routes);
        Assert.Equal("offer_events", route.ProxyName);
        Assert.Equal(["events-r"], route.AnyScopesPredicate!);
        Assert.Null(route.AllScopesPredicate);
    }

    [Fact]
    public void LoadProxyConfig_appends_globalRequiredScopes_to_every_route()
    {
        var config = Config(new()
        {
            ["globalRequiredScopes:0"] = "admin",
            ["proxyConfig:0:ProxyName"] = "a",
            ["proxyConfig:0:ApiUrl"] = "v1/a",
            ["proxyConfig:1:ProxyName"] = "b",
            ["proxyConfig:1:ApiUrl"] = "v1/b",
            ["proxyConfig:1:AllScopesPredicate:0"] = "audit-r"
        });

        var routes = HttpMessageLogic.LoadProxyConfig(config);

        Assert.Equal(["admin"], routes[0].AllScopesPredicate!);
        Assert.Equal(["audit-r", "admin"], routes[1].AllScopesPredicate!);
    }

    [Fact]
    public void LoadProxyConfig_does_not_duplicate_an_already_present_global_scope()
    {
        var config = Config(new()
        {
            ["globalRequiredScopes:0"] = "admin",
            ["proxyConfig:0:ProxyName"] = "a",
            ["proxyConfig:0:ApiUrl"] = "v1/a",
            ["proxyConfig:0:AllScopesPredicate:0"] = "admin"
        });

        Assert.Equal(["admin"], HttpMessageLogic.LoadProxyConfig(config)[0].AllScopesPredicate!);
    }

    [Fact]
    public void LoadProxyConfig_rejects_a_missing_section()
    {
        Assert.Throws<ArgumentException>(() => HttpMessageLogic.LoadProxyConfig(Config([])));
    }

    [Fact]
    public void GatewayOptions_bind_from_the_gateway_section()
    {
        var config = Config(new()
        {
            ["gateway:headers:scopes"] = "x-scopes",
            ["gateway:headers:userId"] = "x-user",
            ["gateway:enableDebugEndpoint"] = "true"
        });

        var options = config.GetSection(GatewayOptions.SectionName).Get<GatewayOptions>()!;

        Assert.Equal("x-scopes", options.Headers.Scopes);
        Assert.Equal("x-user", options.Headers.UserId);
        Assert.Equal("brand", options.Headers.Brand);      // unset values keep their defaults
        Assert.True(options.EnableDebugEndpoint);
    }

    [Fact]
    public void The_debug_endpoint_is_off_by_default()
    {
        Assert.False(new GatewayOptions().EnableDebugEndpoint);
    }
}
