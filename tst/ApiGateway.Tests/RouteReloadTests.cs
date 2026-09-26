using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ApiGateway.Tests;

/// <summary>
/// Proves the mounted route table is picked up without a restart.
///
/// This is the whole premise of shipping the gateway as a generic public image: the route table is
/// deployment config, not part of the build. If watching a mounted file does not actually work,
/// every route change becomes a pod restart — so it is asserted against a real bind mount rather
/// than assumed.
///
/// Skipped unless RouteFilePath is set (compose supplies it).
/// </summary>
public class RouteReloadTests : IAsyncLifetime
{
    private static readonly string? GatewayUrl = Environment.GetEnvironmentVariable("GatewayHttpUrl");
    private static readonly string? RouteFile = Environment.GetEnvironmentVariable("RouteFilePath");
    private static readonly string MockServerUrl = Environment.GetEnvironmentVariable("MockServerBaseUri") ?? "http://localhost:1090";

    private readonly HttpClient gateway = new() { BaseAddress = new Uri(GatewayUrl ?? "http://localhost") };
    private readonly HttpClient mockServer = new() { BaseAddress = new Uri(MockServerUrl) };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string? _original;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(GatewayUrl) || string.IsNullOrEmpty(RouteFile),
            "GatewayHttpUrl/RouteFilePath not set — compose-backed test.");

        _original = await File.ReadAllTextAsync(RouteFile!, Ct);
        await mockServer.PutAsync("/mockserver/reset", null, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_original != null)
            await File.WriteAllTextAsync(RouteFile!, _original);
        gateway.Dispose();
        mockServer.Dispose();
    }

    private static HttpRequestMessage Get(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("auth-claim-scopes", "events-r");
        return request;
    }

    [Fact]
    public async Task A_route_added_to_the_mounted_file_starts_serving_without_a_restart()
    {
        (await mockServer.PutAsJsonAsync("/mockserver/expectation", new
        {
            httpRequest = new { path = "/upstream/v1/added" },
            httpResponse = new { statusCode = 200, body = "added-route-body" }
        }, Ct)).EnsureSuccessStatusCode();

        // Not in the route table yet.
        Assert.Equal(HttpStatusCode.NotFound, (await gateway.SendAsync(Get("/v1/added"), Ct)).StatusCode);

        await AddRouteToMountedFile("v1/added");

        // The gateway polls the mounted file; allow for the poll interval plus the rebuild.
        var response = await PollUntil(() => gateway.SendAsync(Get("/v1/added"), Ct),
            r => r.StatusCode == HttpStatusCode.OK,
            TimeSpan.FromSeconds(60));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("added-route-body", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task An_invalid_route_file_is_rejected_and_the_previous_table_keeps_serving()
    {
        (await mockServer.PutAsJsonAsync("/mockserver/expectation", new
        {
            httpRequest = new { path = "/upstream/v1/event" },
            httpResponse = new { statusCode = 200, body = "still-serving" }
        }, Ct)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await gateway.SendAsync(Get("/v1/event"), Ct)).StatusCode);

        // A bad config push must not take the gateway down or empty its route table. The failure
        // mode this guards is specific: the configuration provider clears its own data before
        // reporting a parse error, so a naive reload swaps in a ZERO-route table and every route
        // starts 404ing. Replace by rename, as Kubernetes does when updating a ConfigMap.
        var temp = RouteFile! + ".tmp";
        await File.WriteAllTextAsync(temp, "{ this is not valid json", Ct);
        File.Move(temp, RouteFile!, overwrite: true);
        await Task.Delay(TimeSpan.FromSeconds(18), Ct);

        var response = await gateway.SendAsync(Get("/v1/event"), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("still-serving", await response.Content.ReadAsStringAsync(Ct));

        // ...and it must recover once valid config is restored, not stay wedged on the old table.
        await File.WriteAllTextAsync(temp, _original!, Ct);
        File.Move(temp, RouteFile!, overwrite: true);

        (await mockServer.PutAsJsonAsync("/mockserver/expectation", new
        {
            httpRequest = new { path = "/upstream/v1/nav" },
            httpResponse = new { statusCode = 200, body = "recovered" }
        }, Ct)).EnsureSuccessStatusCode();

        var recovered = await PollUntil(() => gateway.SendAsync(Get("/v1/nav"), Ct),
            r => r.StatusCode == HttpStatusCode.OK, TimeSpan.FromSeconds(60));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    /// <summary>Rewrites the mounted file with an extra route appended to proxyConfig.</summary>
    private async Task AddRouteToMountedFile(string apiUrl)
    {
        using var doc = JsonDocument.Parse(_original!);
        var routes = doc.RootElement.GetProperty("proxyConfig").EnumerateArray()
            .Select(r => r.Clone()).ToList();

        var updated = new Dictionary<string, object?>();
        foreach (var prop in doc.RootElement.EnumerateObject())
            updated[prop.Name] = prop.Value.Clone();

        var newRoute = JsonSerializer.Deserialize<JsonElement>($$"""
            { "ProxyName": "upstream", "ApiUrl": "{{apiUrl}}", "ProxyUrl": "{{apiUrl}}",
              "AnyScopesPredicate": [ "events-r" ] }
            """);

        updated["proxyConfig"] = routes.Append(newRoute).ToList();

        // Write via a temp file and move, so the gateway never observes a half-written file.
        var temp = RouteFile! + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(updated), Ct);
        File.Move(temp, RouteFile!, overwrite: true);
    }

    private static async Task<HttpResponseMessage> PollUntil(
        Func<Task<HttpResponseMessage>> send, Func<HttpResponseMessage, bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        HttpResponseMessage response;
        do
        {
            response = await send();
            if (done(response))
                return response;
            await Task.Delay(1000);
        }
        while (DateTime.UtcNow < deadline);

        return response;
    }
}
