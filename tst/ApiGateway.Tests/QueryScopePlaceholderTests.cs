using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ApiGateway.Tests;

/// <summary>
/// The <c>{query:name}</c> scope placeholder: the same job as <c>{path:name}</c> for a route that names the
/// resource it acts on in the query string rather than the path.
///
/// lode's customer admin routes are the case it exists for — every user-info-authentik endpoint behind them
/// takes a required <c>?brand=</c> and acts on it, so <c>brand-r:{query:brand}</c> gates an admin to their own
/// brand while checking the very value the service uses. That single-input property is the whole safety
/// argument, and the fail-closed cases below are what keep it true.
/// </summary>
public class QueryScopePlaceholderTests
{
    [Theory]
    [InlineData("?brand=acme", "brand-r:acme")]
    [InlineData("?brand=acme&email=a@b.c", "brand-r:acme")]
    [InlineData("?Brand=acme", "brand-r:acme")]                 // ASP.NET binds ?Brand= too, so the check must see it
    [InlineData("?brand=acme%20co", "brand-r:acme co")]
    public void Query_placeholders_resolve_from_the_query_string(string query, string expected)
        => Assert.Equal(expected, Substitute("brand-r:{query:brand}", query));

    [Theory]
    [InlineData("")]                                            // absent
    [InlineData("?brand=")]                                     // present but empty
    [InlineData("?email=a@b.c")]                                // a different parameter
    [InlineData("?brand=acme&brand=other")]                     // repeated — see below
    [InlineData("?brand=other&brand=acme")]
    public void An_unresolvable_parameter_keeps_its_braces(string query)
        // Never an empty substitution: "brand-r:" scopes nothing and a caller can hold it. A repeated
        // parameter is unresolvable on purpose — which repeat the service binds is its own business, so
        // neither can be checked on its behalf, which is the request-splitting trick this must resist.
        => Assert.Equal("brand-r:{query:brand}", Substitute("brand-r:{query:brand}", query));

    [Fact]
    public void Several_placeholders_in_one_pattern_resolve_independently()
        => Assert.Equal("brand-r:acme:notes", Substitute("brand-r:{query:brand}:{query:scope}", "?brand=acme&scope=notes"));

    [Fact]
    public void An_unresolvable_placeholder_does_not_stop_the_next_one()
        => Assert.Equal("brand-r:{query:brand}:notes", Substitute("brand-r:{query:brand}:{query:scope}", "?scope=notes"));

    [Theory]
    [InlineData("brand-r:acme", "?brand=acme", true)]
    [InlineData("brand-r:acme", "?brand=other", false)]
    [InlineData("brand-r:*", "?brand=other", true)]
    [InlineData("brand-r:", "", false)]
    public async Task A_query_scoped_predicate_gates_on_the_brand_in_the_query(string scopes, string query, bool allowed)
    {
        var (logic, proxy, config) = Build(new()
        {
            ["proxyConfig:0:ApiUrl"] = "v1/admin/customerSearch",
            ["proxyConfig:0:ProxyUrl"] = "v1/customer/search",
            ["proxyConfig:0:AnyScopesPredicate:0"] = "brand-r:{query:brand}",
            ["proxyConfig:0:AnyScopesPredicate:1"] = "brand-r:*",
        });
        var request = new DefaultHttpContext().Request;
        request.Path = "/v1/admin/customerSearch";
        request.QueryString = new QueryString(query);
        request.Headers["brand"] = "acme";   // the caller's own brand must not decide this check

        Assert.Equal(allowed, await logic.IsAuthValid(scopes, config[0], "u1", proxy, request, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("customers-r,brand-r:acme", true)]
    [InlineData("customers-w,brand-r:acme", true)]
    [InlineData("customers-r,brand-r:*", true)]
    [InlineData("brand-r:acme", false)]                          // no customers scope at all
    [InlineData("customers-r,brand-r:other", false)]             // right job, wrong brand
    public async Task Both_groups_are_required_when_one_is_an_All_entry_with_alternatives(string scopes, bool allowed)
    {
        // The shape lode's customer admin routes need: (customers-r or customers-w) and (brand or wildcard).
        // Any is the only OR list, so the second group travels as an "a|b" All entry.
        var (logic, proxy, config) = Build(new()
        {
            ["proxyConfig:0:ApiUrl"] = "v1/admin/customerSearch",
            ["proxyConfig:0:ProxyUrl"] = "v1/customer/search",
            ["proxyConfig:0:AnyScopesPredicate:0"] = "brand-r:{query:brand}",
            ["proxyConfig:0:AnyScopesPredicate:1"] = "brand-r:*",
            ["proxyConfig:0:AllScopesPredicate:0"] = "customers-r|customers-w",
        });
        var request = new DefaultHttpContext().Request;
        request.Path = "/v1/admin/customerSearch";
        request.QueryString = new QueryString("?brand=acme");

        Assert.Equal(allowed, await logic.IsAuthValid(scopes, config[0], "u1", proxy, request, TestContext.Current.CancellationToken));
    }

    private static string Substitute(string pattern, string queryString)
    {
        var request = new DefaultHttpContext().Request;
        request.QueryString = new QueryString(queryString);
        return HttpMessageLogic.SubstituteQueryParameters(pattern, request.Query);
    }

    private static (HttpMessageLogic, Proxy, ProxyConfig[]) Build(Dictionary<string, string?> route)
    {
        route["EndPoints:svc"] = "http://svc:8080/";
        route["proxyConfig:0:ProxyName"] = "svc";
        var config = new ConfigurationBuilder().AddInMemoryCollection(route).Build();
        var options = Options.Create(new GatewayOptions());
        var proxy = new Proxy(config, new NoHttpClients());
        var logic = new HttpMessageLogic(proxy, NullLoggerFactory.Instance,
            new RouteTableProvider(config, options, NullLoggerFactory.Instance), options);
        return (logic, proxy, HttpMessageLogic.LoadProxyConfig(config));
    }

    private sealed class NoHttpClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
