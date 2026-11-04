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
/// <c>Async</c> routes: the load-time rules, and what the gateway sends the scheduler and returns to the client, with
/// the scheduler stubbed. AsyncIntegrationTests runs the same paths against the real scheduler image.
/// </summary>
public class AsyncRouteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string?> AsyncRoute(Action<Dictionary<string, string?>>? change = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["EndPoints:svc"] = "http://svc:8080/base/",
            ["EndPoints:scheduler"] = "http://scheduler:8080/",
            ["proxyConfig:0:ProxyName"] = "svc",
            ["proxyConfig:0:ApiUrl"] = "v1/reports/{id}",
            ["proxyConfig:0:ProxyUrl"] = "v1/report/{id}",
            ["proxyConfig:0:Method"] = "POST",
            ["proxyConfig:0:AnyScopesPredicate:0"] = "reports-w",
            ["proxyConfig:0:Async:MaxWaitSeconds"] = "25",
        };
        change?.Invoke(values);
        return values;
    }

    private static ProxyConfig[] Load(Dictionary<string, string?> values) =>
        HttpMessageLogic.LoadProxyConfig(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    // ---- Load-time rules ----

    [Fact]
    public void An_empty_async_block_binds_with_the_defaults()
    {
        const string json = """
            {
              "EndPoints": { "svc": "http://svc:8080/", "scheduler": "http://scheduler:8080/" },
              "proxyConfig": [
                { "ProxyName": "svc", "ApiUrl": "v1/reports", "ProxyUrl": "v1/reports", "AnyScopesPredicate": [ "reports-w" ], "Async": {} }
              ]
            }
            """;
        var config = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();

        var async = Assert.Single(HttpMessageLogic.LoadProxyConfig(config)).Async;

        Assert.NotNull(async);
        Assert.Equal(25, async.MaxWaitSeconds);
        Assert.Null(async.TimeoutSeconds);
        Assert.Equal(3600, async.ResultTtlSeconds);
    }

    [Theory]
    [InlineData("proxyConfig:0:AnyScopesPredicate:0", null, "needs a scope predicate")]
    [InlineData("proxyConfig:0:Cache:TtlSeconds", "60", "cannot also set Cache")]
    [InlineData("proxyConfig:0:Translate", "true", "cannot also set Translate")]
    [InlineData("proxyConfig:0:ResponsePath", "data", "cannot also set ResponsePath")]
    [InlineData("EndPoints:scheduler", null, "needs an EndPoints:scheduler entry")]
    [InlineData("proxyConfig:0:Async:MaxWaitSeconds", "51", "MaxWaitSeconds must be 0-50")]
    [InlineData("proxyConfig:0:Async:TimeoutSeconds", "31", "TimeoutSeconds must be 1-30")]
    [InlineData("proxyConfig:0:Async:TimeoutSeconds", "0", "TimeoutSeconds must be 1-30")]
    [InlineData("proxyConfig:0:Async:ResultTtlSeconds", "86401", "ResultTtlSeconds must be 1-86400")]
    public void A_route_breaking_an_async_rule_fails_the_load(string key, string? value, string message)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Load(AsyncRoute(v =>
        {
            if (value is null) v.Remove(key);
            else v[key] = value;
        })));
        Assert.Contains("Async route 'v1/reports/{id}' " + message, ex.Message);
    }

    [Fact]
    public void A_global_required_scope_counts_as_the_predicate()
        => Assert.Single(Load(AsyncRoute(v =>
        {
            v.Remove("proxyConfig:0:AnyScopesPredicate:0");
            v["globalRequiredScopes:0"] = "a";
        })));

    [Fact]
    public void The_timeout_limit_follows_gateway_async_maxTimeoutSeconds()
    {
        var route = Assert.Single(Load(AsyncRoute(v =>
        {
            v["proxyConfig:0:Async:TimeoutSeconds"] = "100";
            v["gateway:async:maxTimeoutSeconds"] = "110";
        })));
        Assert.Equal(100, route.Async!.TimeoutSeconds);
    }

    [Fact]
    public void The_scheduler_endpoint_is_not_needed_without_an_async_route()
        => Assert.Single(Load(AsyncRoute(v =>
        {
            v.Remove("EndPoints:scheduler");
            v.Remove("proxyConfig:0:Async:MaxWaitSeconds");
            Assert.DoesNotContain(v.Keys, k => k.Contains("Async"));
        })));

    // ---- Prefer and the enqueue URL ----

    [Theory]
    [InlineData("wait=10", 10)]
    [InlineData("respond-async, wait=5", 5)]
    [InlineData("handling=lenient; wait=\"7\"", 7)]
    [InlineData("WAIT = 3", 3)]
    [InlineData("wait=600", 50)]
    [InlineData("respond-async", null)]
    [InlineData("wait=-1", null)]
    [InlineData("wait=soon", null)]
    public void The_wait_preference_is_read_and_capped_at_50(string prefer, int? expected)
        => Assert.Equal(expected, HttpMessageLogic.PreferredWaitSeconds(prefer));

    [Fact]
    public void The_destination_url_survives_the_schedulers_second_decode()
    {
        var url = HttpMessageLogic.BuildEnqueueUrl("http://svc:8080/v1/report/1?q=a%2Bb&userId=u%201", "POST", "h-1", "u 1",
            new AsyncConfig { TimeoutSeconds = 20, ResultTtlSeconds = 600 });

        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(url[(url.IndexOf('?') + 1)..]);
        // ASP.NET binds the query value once, then the scheduler applies HttpUtility.UrlDecode once more.
        Assert.Equal("http://svc:8080/v1/report/1?q=a%2Bb&userId=u%201", System.Web.HttpUtility.UrlDecode(query["destinationUrl"].ToString()));
        Assert.Equal("POST", query["method"].ToString());
        Assert.Equal("h-1", query["jobKey"].ToString());
        Assert.Equal("u 1", query["owner"].ToString());
        Assert.Equal("true", query["keepResult"].ToString());
        Assert.Equal("600", query["resultTtl"].ToString());
        Assert.Equal("async", query["queue"].ToString());
        Assert.Equal("20", query["timeout"].ToString());
    }

    [Fact]
    public void No_timeout_is_sent_when_the_route_sets_none()
        => Assert.DoesNotContain("timeout=", HttpMessageLogic.BuildEnqueueUrl("http://svc/x", "GET", "h", "u", new AsyncConfig()));

    // ---- The call ----

    [Fact]
    public async Task Without_Prefer_the_job_is_enqueued_and_the_client_gets_a_handle()
    {
        var sent = new List<(HttpRequestMessage Request, string? Body)>();
        var (status, headers, body) = await Call(r => r.RequestUri!.AbsolutePath.EndsWith("/enqueue")
            ? Ok("\"1\"")
            : throw new InvalidOperationException("no result read without Prefer"), sent);

        Assert.Equal(202, status);
        Assert.Equal("1", headers.RetryAfter.ToString());
        var json = JsonDocument.Parse(body).RootElement;
        var handle = json.GetProperty("handle").GetString()!;
        Assert.True(Guid.TryParse(handle, out var guid) && guid.Version == 4);
        Assert.Equal("Enqueued", json.GetProperty("state").GetString());

        var (enqueue, enqueueBody) = Assert.Single(sent);
        Assert.Equal(HttpMethod.Post, enqueue.Method);
        Assert.Equal("scheduler", enqueue.RequestUri!.Host);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(enqueue.RequestUri.Query);
        // The rewritten downstream URL, on the downstream's base, with userId and brand from the headers.
        Assert.Equal("http://svc:8080/base/v1/report/42?x=1&brand=acme&userId=u1",
            System.Web.HttpUtility.UrlDecode(query["destinationUrl"].ToString()));
        Assert.Equal("POST", query["method"].ToString());
        Assert.Equal(handle, query["jobKey"].ToString());
        Assert.Equal("u1", query["owner"].ToString());
        Assert.Equal(handle, enqueue.Headers.GetValues("Idempotency-Key").Single());
        Assert.Equal("u1", enqueue.Headers.GetValues("userid").Single());
        Assert.False(enqueue.Headers.Contains("Prefer"));
        Assert.Equal("""{"title":"q1"}""", enqueueBody);
        Assert.Equal("application/json", enqueue.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task With_Prefer_a_completed_result_is_returned_as_the_downstream_sent_it()
    {
        var sent = new List<(HttpRequestMessage Request, string? Body)>();
        var (status, headers, body) = await Call(r => r.RequestUri!.AbsolutePath.EndsWith("/enqueue")
            ? Ok("\"1\"")
            : Stored(HttpStatusCode.Created, "Completed", """{"id":"r1"}"""), sent, prefer: "wait=30");

        Assert.Equal(201, status);
        Assert.Equal("""{"id":"r1"}""", body);
        Assert.Equal("application/json; charset=utf-8", headers.ContentType.ToString());

        var read = sent[1].Request;
        Assert.Equal(HttpMethod.Get, read.Method);
        var handle = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(sent[0].Request.RequestUri!.Query)["jobKey"].ToString();
        Assert.Equal($"/v1/jobs/{handle}/result", read.RequestUri!.AbsolutePath);
        // The route caps the client's 30 at its MaxWaitSeconds.
        Assert.Equal("?userId=u1&wait=25", read.RequestUri.Query);
    }

    [Fact]
    public async Task A_completed_downstream_5xx_gets_the_standard_error()
    {
        var (status, _, body) = await Call(r => r.RequestUri!.AbsolutePath.EndsWith("/enqueue")
            ? Ok("\"1\"")
            : Stored(HttpStatusCode.InternalServerError, "Completed", """{"detail":"Npgsql: 10.0.0.4 refused"}"""), prefer: "wait=5");

        Assert.Equal(500, status);
        Assert.Equal("Http5xx", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("Npgsql", body);
    }

    [Fact]
    public async Task A_scheduler_failure_is_a_standard_502()
    {
        var (status, _, body) = await Call(r => r.RequestUri!.AbsolutePath.EndsWith("/enqueue")
            ? Ok("\"1\"")
            : Stored(HttpStatusCode.BadGateway, "Failed", """{"handle":"h","state":"Failed","error":"ResultTooLarge"}"""), prefer: "wait=5");

        Assert.Equal(502, status);
        Assert.Equal("Http5xx", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("ResultTooLarge", body);
    }

    [Fact]
    public async Task A_job_still_running_when_the_wait_ends_returns_its_handle_and_state()
    {
        var (status, headers, body) = await Call(r => r.RequestUri!.AbsolutePath.EndsWith("/enqueue")
            ? Ok("\"1\"")
            : Stored(HttpStatusCode.Accepted, "Processing", """{"handle":"h","state":"Processing"}"""), prefer: "wait=5");

        Assert.Equal(202, status);
        Assert.Equal("1", headers.RetryAfter.ToString());
        Assert.Equal("Processing", JsonDocument.Parse(body).RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task An_unreachable_scheduler_is_a_standard_502()
    {
        var (status, _, body) = await Call(_ => throw new HttpRequestException("Connection refused (scheduler:8080)"));

        Assert.Equal(502, status);
        Assert.Equal("Http5xx", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("scheduler", body);
    }

    [Fact]
    public async Task A_refused_enqueue_is_a_standard_502()
    {
        var (status, _, body) = await Call(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("timeout must be 1-30 seconds"),
        });

        Assert.Equal(502, status);
        Assert.DoesNotContain("timeout", body);
    }

    [Fact]
    public async Task A_failed_result_read_still_returns_the_handle()
    {
        var (status, _, body) = await Call(r => r.RequestUri!.AbsolutePath.EndsWith("/enqueue")
            ? Ok("\"1\"")
            : throw new HttpRequestException("reset"), prefer: "wait=5");

        Assert.Equal(202, status);
        Assert.Equal("Enqueued", JsonDocument.Parse(body).RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_caller_without_a_user_id_is_refused_before_anything_is_enqueued()
    {
        var sent = new List<(HttpRequestMessage Request, string? Body)>();
        var (status, _, _) = await Call(_ => Ok("\"1\""), sent, userId: null);

        Assert.Equal(403, status);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task A_caller_missing_the_scope_is_refused_before_anything_is_enqueued()
    {
        var sent = new List<(HttpRequestMessage Request, string? Body)>();
        var (status, _, _) = await Call(_ => Ok("\"1\""), sent, scopes: "other");

        Assert.Equal(403, status);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task Retry_After_from_a_downstream_reaches_the_client()
    {
        // The poll route is a plain route to the scheduler; its 202 carries Retry-After.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EndPoints:scheduler"] = "http://scheduler:8080/",
            ["proxyConfig:0:ProxyName"] = "scheduler",
            ["proxyConfig:0:ApiUrl"] = "v1/async/{handle}",
            ["proxyConfig:0:ProxyUrl"] = "v1/jobs/{handle}/result",
            ["proxyConfig:0:Method"] = "GET",
            ["proxyConfig:0:AnyScopesPredicate:0"] = "reports-w",
        }).Build();
        var (status, headers, _) = await Invoke(config, _ => Stored(HttpStatusCode.Accepted, "Enqueued", """{"handle":"h","state":"Enqueued"}"""),
            [], "GET", "/v1/async/h", "v1/async/h", null, "u1", "reports-w", null);

        Assert.Equal(202, status);
        Assert.Equal("1", headers.RetryAfter.ToString());
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Stored(HttpStatusCode status, string state, string body)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        response.Headers.Add("X-Job-State", state);
        if (status == HttpStatusCode.Accepted)
            response.Headers.Add("Retry-After", "1");
        return response;
    }

    private static Task<(int, IHeaderDictionary, string)> Call(Func<HttpRequestMessage, HttpResponseMessage> respond,
        List<(HttpRequestMessage, string?)>? sent = null, string? prefer = null, string? userId = "u1", string scopes = "reports-w")
        => Invoke(new ConfigurationBuilder().AddInMemoryCollection(AsyncRoute()).Build(), respond, sent ?? [],
            "POST", "/v1/reports/42", "v1/reports/42", "?x=1&userId=someone-else", userId, scopes, prefer);

    private static async Task<(int, IHeaderDictionary, string)> Invoke(IConfiguration config, Func<HttpRequestMessage, HttpResponseMessage> respond,
        List<(HttpRequestMessage, string?)> sent, string method, string path, string slug, string? query, string? userId, string scopes, string? prefer)
    {
        var options = Options.Create(new GatewayOptions());
        var handler = new StubHandler(r =>
        {
            sent.Add((r, r.Content?.ReadAsStringAsync().GetAwaiter().GetResult()));
            return respond(r);
        });
        var proxy = new Proxy(new EndpointDirectory(config, options, new DnsEndpointResolver(), NullLoggerFactory.Instance), new Clients(handler));
        var logic = new HttpMessageLogic(proxy, NullLoggerFactory.Instance, new RouteTableProvider(config, options, NullLoggerFactory.Instance), options);

        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Request.Method = method;
        context.Request.Path = path;
        if (query != null)
            context.Request.QueryString = new QueryString(query);
        context.Request.Headers["auth-claim-scopes"] = scopes;
        context.Request.Headers["brand"] = "acme";
        if (userId != null)
            context.Request.Headers["userid"] = userId;
        if (prefer != null)
            context.Request.Headers["Prefer"] = prefer;
        if (method == "POST")
        {
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream("""{"title":"q1"}"""u8.ToArray());
        }
        var output = new MemoryStream();
        context.Response.Body = output;

        var result = await logic.ProcessCall(context.Request, slug);
        await result.ExecuteAsync(context);

        return (context.Response.StatusCode, context.Response.Headers, Encoding.UTF8.GetString(output.ToArray()));
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        // No BaseAddress: Proxy fills it from the endpoint the client is named after.
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
