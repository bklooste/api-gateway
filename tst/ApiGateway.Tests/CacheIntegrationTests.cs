using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ApiGateway.Tests;

/// <summary>
/// End-to-end proxying through the real cache container (ghcr.io/bklooste/http-rediscache) against a
/// MockServer upstream, as composed by test.compose.yml.
///
/// These are skipped unless GatewayHttpUrl is set, so a plain `dotnet test` stays green without Docker.
/// Run them with: docker compose -f test.compose.yml up --abort-on-container-exit
/// </summary>
public class CacheIntegrationTests : IAsyncLifetime
{
    private static readonly string? GatewayUrl = Environment.GetEnvironmentVariable("GatewayHttpUrl");
    private static readonly string MockServerUrl = Environment.GetEnvironmentVariable("MockServerBaseUri") ?? "http://localhost:1090";

    private readonly HttpClient gateway = new() { BaseAddress = new Uri(GatewayUrl ?? "http://localhost") };
    private readonly HttpClient mockServer = new() { BaseAddress = new Uri(MockServerUrl) };

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(GatewayUrl), "GatewayHttpUrl not set — compose-backed test.");
        await WaitForGatewayReady();
        await mockServer.PutAsync("/mockserver/reset", null, Ct);
    }

    /// <summary>
    /// Compose only guarantees the container started, and the chiseled image carries no shell for a
    /// healthcheck, so readiness is polled here rather than assumed.
    /// </summary>
    private async Task WaitForGatewayReady()
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if ((await gateway.GetAsync("/health/ready")).IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException) { }    // container not listening yet

            await Task.Delay(500);
        }

        Assert.Fail($"Gateway at {GatewayUrl} was not ready within 60s.");
    }

    public ValueTask DisposeAsync()
    {
        gateway.Dispose();
        mockServer.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Registers a MockServer expectation returning <paramref name="body"/> for a path.</summary>
    private async Task ExpectUpstream(string path, string body) =>
        (await mockServer.PutAsJsonAsync("/mockserver/expectation", new
        {
            httpRequest = new { path },
            httpResponse = new
            {
                statusCode = 200,
                headers = new Dictionary<string, string[]> { ["Content-Type"] = ["application/json"] },
                body
            }
        }, Ct)).EnsureSuccessStatusCode();

    /// <summary>How many requests MockServer has received for a path.</summary>
    private async Task<int> UpstreamHits(string path)
    {
        var response = await mockServer.PutAsJsonAsync("/mockserver/retrieve?type=REQUESTS", new { path }, Ct);
        var json = await response.Content.ReadAsStringAsync(Ct);
        return JsonDocument.Parse(json).RootElement.GetArrayLength();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpRequestMessage Get(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        // Identity headers the authenticating proxy would have set.
        request.Headers.Add("auth-claim-scopes", "events-r");
        request.Headers.Add("userid", "user-1");
        request.Headers.Add("brand", "testbrand");
        return request;
    }

    [Fact]
    public async Task An_uncached_route_reaches_the_upstream_and_returns_its_body()
    {
        await ExpectUpstream("/upstream/v1/event", """{"id":"e1"}""");

        var response = await gateway.SendAsync(Get("/v1/event"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"id":"e1"}""", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_cached_route_hits_the_upstream_only_once()
    {
        await ExpectUpstream("/upstream/v1/nav", """{"nav":"first"}""");

        var first = await gateway.SendAsync(Get("/v1/nav?brand=testbrand"), Ct);
        var second = await gateway.SendAsync(Get("/v1/nav?brand=testbrand"), Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(Ct), await second.Content.ReadAsStringAsync(Ct));

        // The second response came from the cache container, not the upstream.
        Assert.Equal(1, await UpstreamHits("/upstream/v1/nav"));
    }

    [Fact]
    public async Task A_caller_missing_the_required_scope_is_rejected_before_the_upstream_is_called()
    {
        await ExpectUpstream("/upstream/v1/event", """{"id":"e1"}""");

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/event");
        request.Headers.Add("auth-claim-scopes", "something-else");

        var response = await gateway.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await UpstreamHits("/upstream/v1/event"));
    }

    [Fact]
    public async Task A_client_supplied_userId_does_not_reach_the_upstream()
    {
        await ExpectUpstream("/upstream/v1/event", """{"id":"e1"}""");

        // The caller tries to act as someone else by supplying the identity params directly.
        var response = await gateway.SendAsync(Get("/v1/event?userId=attacker&brand=other-brand"), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var received = await mockServer.PutAsJsonAsync("/mockserver/retrieve?type=REQUESTS",
            new { path = "/upstream/v1/event" }, Ct);
        var json = await received.Content.ReadAsStringAsync(Ct);

        // Only the header-derived values may survive; the client's must be gone entirely, not
        // merely ordered after the trusted ones.
        Assert.DoesNotContain("attacker", json);
        Assert.DoesNotContain("other-brand", json);
        Assert.Contains("user-1", json);
    }

    [Fact]
    public async Task An_unrouted_path_is_not_proxied()
    {
        var response = await gateway.SendAsync(Get("/v1/not-in-config"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_debug_endpoint_is_not_exposed_by_default()
    {
        // It echoes the trusted identity headers back, so it must stay off unless explicitly enabled.
        var response = await gateway.SendAsync(Get("/debug"), Ct);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
