using System.Text;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.OpenApi;

namespace ApiGateway.Tests;

/// <summary>
/// The merger must read every OpenAPI version a downstream service can emit (3.0, 3.1, 3.2) and merge
/// them into one document. Microsoft.OpenApi 2.x could not read 3.2, so a 3.2 service's paths silently
/// vanished from the merged document. In-process: no HTTP, no compose.
/// </summary>
public class OpenApiMergeVersionTests
{
    // One fixture per version, each with one path under its own ProxyUrl and one component schema.
    // aspnetcore-11-openapi-3.2.json was captured from a real .NET 11 Microsoft.AspNetCore.OpenApi
    // (11.0.0-rc.2) host with OpenApiVersion = OpenApi3_2, so 3.2 is not only hand-written.
    private static readonly (string File, string ProxyPath, string ApiPath, string Schema)[] Cases =
    [
        ("openapi-3.0.1.json", "/v1/legacy/items", "/api/v1/legacy/items", "LegacyItem"),
        ("openapi-3.1.1.json", "/v1/orders", "/api/v1/orders", "Order"),
        ("openapi-3.2.0.json", "/v1/markets", "/api/v1/markets", "Market"),
        ("aspnetcore-11-openapi-3.2.json", "/v1/tags/{id}", "/api/v1/tags/{id}", "TagItem"),
    ];

    public static TheoryData<string, string, string> Fixtures =>
        new(Cases.Select(c => (c.File, c.ProxyPath, c.Schema)));

    private static readonly ProxyConfig[] Routes =
    [
        new() { ProxyName = "legacy", ProxyUrl = "/v1/legacy/items", ApiUrl = "/api/v1/legacy/items" },
        new() { ProxyName = "orders", ProxyUrl = "/v1/orders", ApiUrl = "/api/v1/orders" },
        new() { ProxyName = "markets", ProxyUrl = "/v1/markets", ApiUrl = "/api/v1/markets" },
        new() { ProxyName = "tags", ProxyUrl = "/v1/tags/{id}", ApiUrl = "/api/v1/tags/{id}" },
    ];

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "OpenApi", name));

    private static Task<OpenApiDocument?> Load(string json) =>
        OpenApiMerger.LoadDownstreamAsync(json, "test", NullLogger.Instance);

    private static OpenApiDocument EmptyGatewayDocument() => new()
    {
        Info = new OpenApiInfo { Title = "gateway", Version = "v1" },
        Paths = new OpenApiPaths(),
        Components = new OpenApiComponents { Schemas = new Dictionary<string, IOpenApiSchema>() },
    };

    private static async Task<OpenApiDocument> MergeAll(params string[] fixtures)
    {
        var docs = new List<OpenApiDocument?>();
        foreach (var f in fixtures)
            docs.Add(await Load(Fixture(f)));
        var merged = EmptyGatewayDocument();
        OpenApiMerger.Merge(merged, docs, Routes);
        return merged;
    }

    private static string[] AllFixtures => Cases.Select(c => c.File).ToArray();

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Each_version_loads_with_no_diagnostic_errors(string fixture, string proxyPath, string schema)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Fixture(fixture)));
        var result = await OpenApiDocument.LoadAsync(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Diagnostic!.Errors);
        Assert.Contains(proxyPath, result.Document!.Paths.Keys);
        Assert.Contains(schema, result.Document.Components!.Schemas!.Keys);
        Assert.NotNull(await Load(Fixture(fixture)));
    }

    [Fact]
    public async Task Merging_all_versions_rewrites_every_path_from_ProxyUrl_to_ApiUrl()
    {
        var merged = await MergeAll(AllFixtures);

        foreach (var c in Cases)
        {
            Assert.Contains(c.ApiPath, merged.Paths.Keys);
            Assert.DoesNotContain(c.ProxyPath, merged.Paths.Keys);
        }
    }

    [Fact]
    public async Task Merging_all_versions_keeps_every_schema()
    {
        var merged = await MergeAll(AllFixtures);

        foreach (var c in Cases)
            Assert.Contains(c.Schema, merged.Components!.Schemas!.Keys);
    }

    [Theory]
    [InlineData(OpenApiSpecVersion.OpenApi3_0, "3.0")]
    [InlineData(OpenApiSpecVersion.OpenApi3_1, "3.1")]
    [InlineData(OpenApiSpecVersion.OpenApi3_2, "3.2")]
    public async Task Merged_document_round_trips_through_the_reader(OpenApiSpecVersion version, string prefix)
    {
        var merged = await MergeAll(AllFixtures);

        var json = await merged.SerializeAsJsonAsync(version, TestContext.Current.CancellationToken);
        Assert.Contains($"\"openapi\": \"{prefix}", json);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var reread = await OpenApiDocument.LoadAsync(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(reread.Diagnostic!.Errors);
        foreach (var c in Cases)
        {
            Assert.Contains(c.ApiPath, reread.Document!.Paths.Keys);
            Assert.Contains(c.Schema, reread.Document.Components!.Schemas!.Keys);
        }
    }

    [Fact]
    public async Task An_unreadable_document_is_skipped_and_the_others_still_merge()
    {
        var bad = Fixture("openapi-3.1.1.json").Replace("\"3.1.1\"", "\"9.9.9\"");
        Assert.Null(await Load(bad));

        var merged = EmptyGatewayDocument();
        OpenApiMerger.Merge(merged,
            [await Load(Fixture("openapi-3.0.1.json")), await Load(bad), await Load(Fixture("openapi-3.2.0.json"))],
            Routes);

        Assert.Contains("/api/v1/legacy/items", merged.Paths.Keys);
        Assert.Contains("/api/v1/markets", merged.Paths.Keys);
        Assert.DoesNotContain("/api/v1/orders", merged.Paths.Keys);
        Assert.Contains("LegacyItem", merged.Components!.Schemas!.Keys);
        Assert.Contains("Market", merged.Components.Schemas.Keys);
    }
}
