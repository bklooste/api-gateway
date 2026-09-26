using Microsoft.Extensions.Configuration;

namespace ApiGateway.Tests;

/// <summary>
/// The precomputed flags that let the per-request auth path skip work. Getting these wrong is a
/// security bug, not a performance one: a route wrongly marked as having no predicates would stop
/// being checked at all.
/// </summary>
public class AuthFastPathTests
{
    private static ProxyConfig[] Load(Dictionary<string, string?> values) =>
        HttpMessageLogic.LoadProxyConfig(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    private static Dictionary<string, string?> Route(params (string Key, string Value)[] extra)
    {
        var d = new Dictionary<string, string?>
        {
            ["proxyConfig:0:ProxyName"] = "svc",
            ["proxyConfig:0:ApiUrl"] = "v1/a"
        };
        foreach (var (k, v) in extra)
            d[k] = v;
        return d;
    }

    [Fact]
    public void A_route_with_no_predicates_is_marked_as_needing_no_checks()
    {
        var route = Load(Route())[0];

        Assert.False(route.HasPredicates);
        Assert.False(route.HasPlaceholders);
        Assert.False(route.NeedsResourceLookup);
    }

    [Fact]
    public void Fixed_predicates_need_no_substitution()
    {
        var route = Load(Route(("proxyConfig:0:AnyScopesPredicate:0", "events-r")))[0];

        Assert.True(route.HasPredicates);
        Assert.False(route.HasPlaceholders);
    }

    [Theory]
    [InlineData("bet-w:b={brand}")]
    [InlineData("user:{userId}")]
    [InlineData("x:{0}")]
    public void Placeholder_predicates_are_detected(string pattern)
    {
        var route = Load(Route(("proxyConfig:0:AllScopesPredicate:0", pattern)))[0];

        Assert.True(route.HasPredicates);
        Assert.True(route.HasPlaceholders);
    }

    [Fact]
    public void A_resource_lookup_needs_both_the_placeholder_and_the_ResourceAuth_block()
    {
        var withBoth = Load(Route(
            ("proxyConfig:0:AllScopesPredicate:0", "bet-w:b={resource:Brand}"),
            ("proxyConfig:0:ResourceAuth:Service", "bet"),
            ("proxyConfig:0:ResourceAuth:Url", "state/{betId}")))[0];
        Assert.True(withBoth.NeedsResourceLookup);

        // Placeholder but nothing configured to resolve it — must not attempt a lookup.
        var noBlock = Load(Route(("proxyConfig:0:AllScopesPredicate:0", "bet-w:b={resource:Brand}")))[0];
        Assert.False(noBlock.NeedsResourceLookup);

        // Configured but unused — must not pay for a lookup on every request.
        var unused = Load(Route(
            ("proxyConfig:0:AllScopesPredicate:0", "bet-w"),
            ("proxyConfig:0:ResourceAuth:Service", "bet")))[0];
        Assert.False(unused.NeedsResourceLookup);
    }

    [Fact]
    public void A_globalRequiredScope_makes_an_otherwise_bare_route_require_checks()
    {
        // The flags must be derived AFTER the global merge. Deriving them from the raw bound config
        // would mark this route as needing no authorization, silently dropping the "admin" gate.
        var route = Load(new Dictionary<string, string?>
        {
            ["globalRequiredScopes:0"] = "admin",
            ["proxyConfig:0:ProxyName"] = "svc",
            ["proxyConfig:0:ApiUrl"] = "v1/a"
        })[0];

        Assert.True(route.HasPredicates);
        Assert.Equal(["admin"], route.AllScopesPredicate!);
    }
}
