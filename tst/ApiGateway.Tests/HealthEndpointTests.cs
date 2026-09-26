using System.Net;

namespace ApiGateway.Tests;

/// <summary>
/// The health endpoints must be answered by the gateway itself, never proxied. A bare <c>/health</c> in particular
/// is what test harnesses and compose healthchecks poll; without a mapping the catch-all proxy returns 404.
///
/// Skipped unless GatewayHttpUrl is set (compose supplies it).
/// </summary>
public class HealthEndpointTests
{
    private static readonly string? GatewayUrl = Environment.GetEnvironmentVariable("GatewayHttpUrl");

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    [InlineData("/health/live")]
    public async Task Health_paths_are_served_by_the_gateway_not_proxied(string path)
    {
        Assert.SkipWhen(string.IsNullOrEmpty(GatewayUrl), "GatewayHttpUrl not set — compose-backed test.");

        using var http = new HttpClient { BaseAddress = new Uri(GatewayUrl!) };
        var response = await http.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
