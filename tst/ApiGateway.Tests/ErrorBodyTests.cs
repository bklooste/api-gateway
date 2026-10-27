using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ApiGateway.Tests;

/// <summary>
/// Downstream errors: keyed 4xx ProblemDetails pass through, everything else becomes the standard keyed body.
/// </summary>
public class ErrorBodyTests
{
    [Fact]
    public async Task A_5xx_with_an_exception_message_is_replaced()
    {
        var (status, body) = await Call(HttpStatusCode.InternalServerError, """{"detail":"Npgsql: connection to 10.0.0.4:5432 refused","status":500}""");

        Assert.Equal(500, status);
        Assert.Equal("Http5xx", body.GetProperty("code").GetString());
        Assert.DoesNotContain("Npgsql", body.GetRawText());
    }

    [Fact]
    public async Task A_keyed_5xx_is_still_replaced()
    {
        var (status, body) = await Call(HttpStatusCode.ServiceUnavailable, """{"code":"InsufficientFunds","detail":"x"}""");

        Assert.Equal(503, status);
        Assert.Equal("Http5xx", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_unkeyed_4xx_is_replaced_with_its_status_key()
    {
        var (status, body) = await Call(HttpStatusCode.Conflict, "Sequence contains no elements");

        Assert.Equal(409, status);
        Assert.Equal("Http409", body.GetProperty("code").GetString());
        Assert.False(body.TryGetProperty("detail", out _));
    }

    [Fact]
    public async Task A_keyed_4xx_passes_through()
    {
        const string keyed = """{"status":400,"detail":"Stake below the minimum of 1","code":"StakeBelowMinimum","params":{"min":1}}""";
        var (status, body) = await Call(HttpStatusCode.BadRequest, keyed);

        Assert.Equal(400, status);
        Assert.Equal("StakeBelowMinimum", body.GetProperty("code").GetString());
        Assert.Equal(1, body.GetProperty("params").GetProperty("min").GetInt32());
    }

    [Fact]
    public async Task An_unreachable_downstream_is_a_keyed_502()
    {
        var (status, body) = await Call(_ => throw new HttpRequestException("Connection refused (svc:8080)"));

        Assert.Equal(502, status);
        Assert.Equal("Http5xx", body.GetProperty("code").GetString());
        Assert.DoesNotContain("svc:8080", body.GetRawText());
    }

    private static Task<(int, JsonElement)> Call(HttpStatusCode status, string downstreamBody)
        => Call(_ => new HttpResponseMessage(status) { Content = new StringContent(downstreamBody, Encoding.UTF8, "application/json") });

    private static async Task<(int, JsonElement)> Call(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EndPoints:svc"] = "http://svc:8080/",
            ["proxyConfig:0:ProxyName"] = "svc",
            ["proxyConfig:0:ApiUrl"] = "v1/a",
            ["proxyConfig:0:ProxyUrl"] = "v1/a",
        }).Build();
        var options = Options.Create(new GatewayOptions());
        var proxy = new Proxy(new EndpointDirectory(config, options, new DnsEndpointResolver(), NullLoggerFactory.Instance), new Clients(new StubHandler(respond)));
        var logic = new HttpMessageLogic(proxy, NullLoggerFactory.Instance, new RouteTableProvider(config, options, NullLoggerFactory.Instance), options);

        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Request.Method = "GET";
        context.Request.Path = "/v1/a";
        var output = new MemoryStream();
        context.Response.Body = output;

        var result = await logic.ProcessCall(context.Request, "v1/a");
        await result.ExecuteAsync(context);

        return (context.Response.StatusCode, JsonDocument.Parse(output.ToArray()).RootElement.Clone());
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
