using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ApiGateway.Tests;

/// <summary>
/// <c>Async</c> routes end to end: the gateway image, the real scheduler image
/// (ghcr.io/bklooste/async-scheduler/service:0.3.1) and a slow MockServer downstream, as composed by test.compose.yml.
/// The route table (routes/routes.json) has <c>v1/reports/{id}</c> (MaxWaitSeconds 20), <c>v1/quick-reports/{id}</c>
/// (MaxWaitSeconds 1), both to <c>upstream</c> <c>v1/report/{id}</c>, and the poll route <c>v1/async/{handle}</c>.
///
/// Skipped unless GatewayHttpUrl is set, so a plain `dotnet test` stays green without Docker.
/// </summary>
public class AsyncIntegrationTests : IAsyncLifetime
{
    private static readonly string? GatewayUrl = Environment.GetEnvironmentVariable("GatewayHttpUrl");
    private static readonly string MockServerUrl = Environment.GetEnvironmentVariable("MockServerBaseUri") ?? "http://localhost:1090";

    private readonly HttpClient gateway = new() { BaseAddress = new Uri(GatewayUrl ?? "http://localhost") };
    private readonly HttpClient mockServer = new() { BaseAddress = new Uri(MockServerUrl) };

    // A fresh report id per test, so a job left running by one test never answers another's assertions.
    private readonly string id = "r-" + Guid.NewGuid().ToString("N")[..8];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(GatewayUrl), "GatewayHttpUrl not set — compose-backed test.");
        await WaitForReady();
    }

    public ValueTask DisposeAsync()
    {
        gateway.Dispose();
        mockServer.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>The gateway, and the scheduler behind it: an unknown handle is a 404 only once the scheduler answers.</summary>
    private async Task WaitForReady()
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if ((await gateway.SendAsync(Poll(Guid.NewGuid().ToString()), Ct)).StatusCode == HttpStatusCode.NotFound)
                    return;
            }
            catch (HttpRequestException) { }    // gateway not listening yet

            await Task.Delay(500, Ct);
        }

        Assert.Fail($"Gateway at {GatewayUrl} and its scheduler were not ready within 90s.");
    }

    private string UpstreamPath => $"/upstream/v1/report/{id}";

    /// <summary>The slow downstream: answers <paramref name="status"/> with <paramref name="body"/> after <paramref name="delayMs"/>.</summary>
    private async Task ExpectUpstream(int status, string body, int delayMs, string contentType = "application/json") =>
        (await mockServer.PutAsJsonAsync("/mockserver/expectation", new
        {
            httpRequest = new { method = "POST", path = UpstreamPath },
            httpResponse = new
            {
                statusCode = status,
                headers = new Dictionary<string, string[]> { ["Content-Type"] = [contentType] },
                body,
                delay = new { timeUnit = "MILLISECONDS", value = delayMs },
            }
        }, Ct)).EnsureSuccessStatusCode();

    /// <summary>The requests the downstream received, as MockServer's JSON array.</summary>
    private async Task<JsonElement> UpstreamRequests()
    {
        var response = await mockServer.PutAsJsonAsync("/mockserver/retrieve?type=REQUESTS", new { path = UpstreamPath }, Ct);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    private static HttpRequestMessage Submit(string path, string? prefer = null, string scopes = "reports-w")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("""{"period":"2026-10"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("auth-claim-scopes", scopes);
        request.Headers.Add("userid", "user-1");
        request.Headers.Add("brand", "testbrand");
        if (prefer != null)
            request.Headers.Add("Prefer", prefer);
        return request;
    }

    private static HttpRequestMessage Poll(string handle, string userId = "user-1", int? wait = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/async/{handle}" + (wait is { } w ? $"?wait={w}" : ""));
        request.Headers.Add("auth-claim-scopes", "reports-w");
        request.Headers.Add("userid", userId);
        request.Headers.Add("brand", "testbrand");
        return request;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private async Task<string> SubmitForHandle()
    {
        var submitted = await gateway.SendAsync(Submit($"/v1/reports/{id}"), Ct);
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        return (await Json(submitted)).GetProperty("handle").GetString()!;
    }

    [Fact]
    public async Task With_Prefer_wait_the_client_gets_the_slow_downstreams_answer()
    {
        await ExpectUpstream(200, """{"report":"done"}""", delayMs: 2000);

        var clock = Stopwatch.StartNew();
        var response = await gateway.SendAsync(Submit($"/v1/reports/{id}?userId=attacker", prefer: "wait=15"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"report":"done"}""", await response.Content.ReadAsStringAsync(Ct));
        Assert.StartsWith("application/json", response.Content.Headers.ContentType!.ToString());
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(2), $"answered in {clock.Elapsed}, before the downstream did");

        // The downstream got the request a synchronous call would have sent: rewritten path, the body, userId from the
        // header (the client's own is gone), and the handle as an Idempotency-Key.
        var received = Assert.Single((await UpstreamRequests()).EnumerateArray());
        var raw = received.GetRawText();
        Assert.Contains("user-1", raw);
        Assert.DoesNotContain("attacker", raw);
        Assert.Contains("Idempotency-Key", raw);
        Assert.Contains("2026-10", raw);
    }

    [Fact]
    public async Task Without_Prefer_the_client_gets_a_handle_at_once_and_polls_for_the_answer()
    {
        await ExpectUpstream(200, """{"report":"polled"}""", delayMs: 3000);

        var clock = Stopwatch.StartNew();
        var submitted = await gateway.SendAsync(Submit($"/v1/reports/{id}"), Ct);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"202 took {clock.Elapsed}");

        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        Assert.NotNull(submitted.Headers.RetryAfter);
        var body = await Json(submitted);
        var handle = body.GetProperty("handle").GetString()!;
        Assert.True(Guid.TryParse(handle, out _));
        Assert.Equal("Enqueued", body.GetProperty("state").GetString());

        // Still running: the poll says so, with a 202 and Retry-After.
        var early = await gateway.SendAsync(Poll(handle), Ct);
        Assert.Equal(HttpStatusCode.Accepted, early.StatusCode);
        Assert.NotNull(early.Headers.RetryAfter);
        Assert.Equal(handle, (await Json(early)).GetProperty("handle").GetString());

        var done = await gateway.SendAsync(Poll(handle, wait: 15), Ct);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal("""{"report":"polled"}""", await done.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Another_users_poll_gets_the_same_404_as_an_unknown_handle()
    {
        await ExpectUpstream(200, """{"report":"mine"}""", delayMs: 0);
        var handle = await SubmitForHandle();
        Assert.Equal(HttpStatusCode.OK, (await gateway.SendAsync(Poll(handle, wait: 15), Ct)).StatusCode);

        var stranger = await gateway.SendAsync(Poll(handle, userId: "user-2", wait: 1), Ct);
        var unknown = await gateway.SendAsync(Poll(Guid.NewGuid().ToString()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("Http404", (await Json(stranger)).GetProperty("code").GetString());
        Assert.DoesNotContain("mine", await stranger.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_keyed_downstream_4xx_is_relayed()
    {
        await ExpectUpstream(409, """{"status":409,"code":"ReportExists","detail":"already running"}""", delayMs: 500);

        var response = await gateway.SendAsync(Submit($"/v1/reports/{id}", prefer: "wait=15"), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("ReportExists", (await Json(response)).GetProperty("code").GetString());
        // 4xx is final: one call.
        Assert.Equal(1, (await UpstreamRequests()).GetArrayLength());
    }

    [Fact]
    public async Task An_unkeyed_downstream_4xx_gets_the_standard_error()
    {
        await ExpectUpstream(400, "Sequence contains no elements", delayMs: 0, contentType: "text/plain");

        var response = await gateway.SendAsync(Submit($"/v1/reports/{id}", prefer: "wait=15"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("Http400", body);
        Assert.DoesNotContain("Sequence", body);
    }

    [Fact]
    public async Task A_downstream_5xx_is_retried_then_becomes_Http5xx()
    {
        await ExpectUpstream(500, """{"detail":"Npgsql: connection to 10.0.0.4:5432 refused"}""", delayMs: 0);

        var response = await gateway.SendAsync(Submit($"/v1/reports/{id}", prefer: "wait=20"), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("Http5xx", body);
        Assert.DoesNotContain("Npgsql", body);
        // The scheduler's default for a kept-result job: three attempts.
        Assert.Equal(3, (await UpstreamRequests()).GetArrayLength());
    }

    [Fact]
    public async Task A_result_over_the_schedulers_limit_is_a_standard_502()
    {
        // test.compose.yml sets Scheduler__MaxResultBytes=1024.
        await ExpectUpstream(200, "\"" + new string('x', 4096) + "\"", delayMs: 0);

        var response = await gateway.SendAsync(Submit($"/v1/reports/{id}", prefer: "wait=15"), Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("Http5xx", body);
        Assert.DoesNotContain("ResultTooLarge", body);
    }

    [Fact]
    public async Task The_routes_MaxWaitSeconds_caps_the_clients_wait()
    {
        await ExpectUpstream(200, """{"report":"slow"}""", delayMs: 5000);

        var clock = Stopwatch.StartNew();
        var response = await gateway.SendAsync(Submit($"/v1/quick-reports/{id}", prefer: "wait=30"), Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"waited {clock.Elapsed} on a route capped at 1 s");
        Assert.NotNull(response.Headers.RetryAfter);
    }

    [Fact]
    public async Task A_caller_missing_the_scope_is_refused_and_nothing_is_called()
    {
        await ExpectUpstream(200, """{"report":"never"}""", delayMs: 0);

        var response = await gateway.SendAsync(Submit($"/v1/reports/{id}", prefer: "wait=2", scopes: "events-r"), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await Task.Delay(1500, Ct);
        Assert.Equal(0, (await UpstreamRequests()).GetArrayLength());
    }
}
