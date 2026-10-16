using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace ApiGateway.Tests;

/// <summary>
/// <c>gateway:openApi:outputVersion</c> picks the version the merged document is served as (default 3.1),
/// and an unknown value stops startup rather than silently falling back. In-process: the real
/// <see cref="GatewayHost"/> on a loopback port, no compose.
/// </summary>
public class OpenApiOutputVersionTests
{
    private static WebApplication BuildGateway(string? outputVersion)
    {
        var args = new List<string> { "--urls=http://127.0.0.1:0" };
        if (outputVersion != null)
            args.Add($"--gateway:openApi:outputVersion={outputVersion}");
        return GatewayHost.Build([.. args]);
    }

    [Theory]
    [InlineData(null, "3.1")]
    [InlineData("3.0", "3.0")]
    [InlineData("3.1", "3.1")]
    [InlineData("3.2", "3.2")]
    public async Task Merged_document_is_served_in_the_configured_version(string? configured, string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = BuildGateway(configured);
        await app.StartAsync(ct);

        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var json = await http.GetStringAsync("/swagger/v1/swagger.json", ct);

        Assert.Contains($"\"openapi\": \"{expected}.", json);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var reread = await OpenApiDocument.LoadAsync(stream, cancellationToken: ct);
        Assert.Empty(reread.Diagnostic!.Errors);

        await app.StopAsync(ct);
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("3.3")]
    [InlineData("latest")]
    public async Task An_unknown_output_version_fails_startup(string configured)
    {
        // The gateway resolves its options while mapping endpoints, so the failure surfaces from Build;
        // ValidateOnStart would stop StartAsync for any host that resolved them later.
        var ex = await Assert.ThrowsAsync<OptionsValidationException>(async () =>
        {
            await using var app = BuildGateway(configured);
            await app.StartAsync(TestContext.Current.CancellationToken);
        });
        Assert.Contains("outputVersion", ex.Message);
    }
}
