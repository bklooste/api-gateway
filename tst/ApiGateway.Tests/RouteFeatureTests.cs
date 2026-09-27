using System.Net;
using System.Net.Http.Headers;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ApiGateway.Tests;

/// <summary>
/// {brand} in ProxyUrl, {path:name} scope placeholders, the identity query params,
/// and Content-Disposition passing through on downloads.
/// </summary>
public class RouteFeatureTests
{
    [Fact]
    public void RewriteUrl_substitutes_brand_from_the_identity_header()
        => Assert.Equal("v1/brand/acme%20co", HttpMessageLogic.RewriteUrl("v1/brandui", "v1/brandui", "v1/brand/{brand}", brand: "acme co"));

    [Fact]
    public void RewriteUrl_prefers_a_brand_path_parameter_over_the_header()
        => Assert.Equal("v1/brand/other", HttpMessageLogic.RewriteUrl("v1/admin/brand/other", "v1/admin/brand/{brand}", "v1/brand/{brand}", brand: "acme"));

    [Fact]
    public void RewriteUrl_fills_brand_and_userId_together()
        => Assert.Equal("v1/crypto/addresses/acme/u1", HttpMessageLogic.RewriteUrl("v1/me/addresses", "v1/me/addresses", "v1/crypto/addresses/{brand}/{userId}", "u1", "acme"));

    [Theory]
    [InlineData("/v1/admin/brand/acme/payoutLimits", "brand-r:acme")]
    [InlineData("/v1/admin/brand/acme%20co/payoutLimits", "brand-r:acme co")]
    public void Path_placeholders_resolve_from_the_matched_segment(string path, string expected)
        => Assert.Equal(expected, HttpMessageLogic.SubstitutePathParameters("brand-r:{path:brand}", path, "v1/admin/brand/{brand}/payoutLimits"));

    [Fact]
    public void An_unknown_path_placeholder_is_left_unresolved()
        => Assert.Equal("brand-r:{path:tenant}", HttpMessageLogic.SubstitutePathParameters("brand-r:{path:tenant}", "/v1/admin/brand/acme", "v1/admin/brand/{brand}"));

    [Theory]
    [InlineData("brand-r:acme", "/v1/admin/brand/acme", true)]
    [InlineData("brand-r:acme", "/v1/admin/brand/other", false)]
    [InlineData("brand-r:*", "/v1/admin/brand/other", true)]
    public async Task A_path_scoped_predicate_gates_on_the_brand_in_the_url(string scopes, string path, bool allowed)
    {
        var (logic, proxy, config) = Build(new()
        {
            ["proxyConfig:0:ApiUrl"] = "v1/admin/brand/{brand}",
            ["proxyConfig:0:ProxyUrl"] = "v1/brand/{brand}",
            ["proxyConfig:0:AnyScopesPredicate:0"] = "brand-r:{path:brand}",
            ["proxyConfig:0:AnyScopesPredicate:1"] = "brand-r:*",
        });
        var request = new DefaultHttpContext().Request;
        request.Path = path;
        request.Headers["brand"] = "acme";

        Assert.Equal(allowed, await logic.IsAuthValid(scopes, config[0], "u1", proxy, request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UserId_and_brand_are_set_from_the_headers_and_customerId_passes_through()
        => Assert.Equal("http://svc:8080/v1/a?customerId=victim&x=1&brand=acme&userId=u1", await ForwardedUrl(new GatewayOptions()));

    [Fact]
    public async Task With_brand_overwriting_off_the_callers_brand_passes_through()
        => Assert.Equal("http://svc:8080/v1/a?brand=other&customerId=victim&x=1&userId=u1",
            await ForwardedUrl(new GatewayOptions { OverwriteBrandQueryParam = false }));

    [Fact]
    public async Task A_caller_cannot_choose_the_userId()
        => Assert.Equal("http://svc:8080/v1/a?x=1&brand=acme&userId=u1", await ForwardedUrl(new GatewayOptions(), "?userId=victim&x=1"));

    private static async Task<string?> ForwardedUrl(GatewayOptions options, string query = "?brand=other&customerId=victim&x=1")
    {
        string? forwarded = null;
        var (logic, _, _) = Build(new()
        {
            ["proxyConfig:0:ApiUrl"] = "v1/a",
            ["proxyConfig:0:ProxyUrl"] = "v1/a",
        }, new StubHandler(r => { forwarded = r.RequestUri!.ToString(); return new HttpResponseMessage(HttpStatusCode.OK); }), options);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/v1/a";
        context.Request.QueryString = new QueryString(query);
        context.Request.Headers["brand"] = "acme";
        context.Request.Headers["userid"] = "u1";

        await logic.ProcessCall(context.Request, "v1/a");
        return forwarded;
    }

    [Fact]
    public async Task A_download_keeps_its_content_disposition()
    {
        var downstream = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("a,b\n"u8.ToArray()) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "statement.csv" };
            return response;
        });
        var (logic, _, _) = Build(new()
        {
            ["proxyConfig:0:ApiUrl"] = "v2/statementCsv",
            ["proxyConfig:0:ProxyUrl"] = "v2/statementCsv",
        }, downstream);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/v2/statementCsv";

        await logic.ProcessCall(context.Request, "v2/statementCsv");

        Assert.Equal("attachment; filename=statement.csv", context.Response.Headers.ContentDisposition.ToString());
    }

    private static (HttpMessageLogic, Proxy, ProxyConfig[]) Build(Dictionary<string, string?> route, HttpMessageHandler? handler = null, GatewayOptions? gatewayOptions = null)
    {
        route["EndPoints:svc"] = "http://svc:8080/";
        route["proxyConfig:0:ProxyName"] = "svc";
        var config = new ConfigurationBuilder().AddInMemoryCollection(route).Build();
        var options = Options.Create(gatewayOptions ?? new GatewayOptions());
        var proxy = new Proxy(config, new Clients(handler ?? new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));
        var logic = new HttpMessageLogic(proxy, NullLoggerFactory.Instance, new RouteTableProvider(config, options, NullLoggerFactory.Instance), options);
        return (logic, proxy, HttpMessageLogic.LoadProxyConfig(config));
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://svc:8080/") };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
