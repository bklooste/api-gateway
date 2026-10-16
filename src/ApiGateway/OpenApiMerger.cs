using System.Globalization;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace ApiGateway;

/// <summary>
/// Merges the OpenAPI documents of every downstream service the gateway proxies into its own document,
/// rewriting ProxyUrl paths to their client-facing ApiUrl.
/// </summary>
public class OpenApiMerger(Proxy proxy, IHttpContextAccessor httpContext, ILoggerFactory loggerFactory, IConfiguration configuration, IOptions<GatewayOptions> options) : IOpenApiDocumentTransformer
{
    private readonly Proxy proxy = proxy;
    private readonly ILogger logger = loggerFactory.CreateLogger<OpenApiMerger>();
    private readonly ProxyConfig[] config = HttpMessageLogic.LoadProxyConfig(configuration);
    private readonly GatewayOptions _options = options.Value;

    public async Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        // Only fetch documents from services this gateway actually proxies to
        var proxyServiceNames = config.Select(c => c.ProxyName).ToHashSet();

        // Fetch OpenAPI documents of the proxied services in parallel
        var tasks = proxy.EndPointUrls
            .Where(x => proxyServiceNames.Contains(x.Key))
            .Select(async x =>
        {
            try
            {
                var proxyClient = proxy.GetHttpClient(x.Key);
                using var fetchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                fetchCts.CancelAfter(TimeSpan.FromSeconds(_options.OpenApiFetchTimeoutSeconds));
                var json = await proxyClient.GetStringAsync("swagger/v1/swagger.json", fetchCts.Token);
                return (x.Key, Downstream: await LoadDownstreamAsync(json, x.Value?.ToString(), logger));
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Error))
                    logger.LogError(ex, "Error getting OpenAPI document {Message}", ex.Message);
                return (x.Key, Downstream: (OpenApiDocument?)null);
            }
        });

        var results = await Task.WhenAll(tasks);

        Merge(document, results.Select(r => r.Downstream), config);
        proxy.ApiDocument = document;

        // Advertise the externally visible base URL when the gateway sits behind a reverse proxy.
        if (document?.Servers == null)
            return;

        if (httpContext?.HttpContext == null)
            return;
        var httpReq = httpContext.HttpContext.Request;
        var srv = httpReq.Headers["X-Forwarded-Host"];
        if (string.IsNullOrEmpty(srv))
            srv = httpReq.Host.Value;
        var scheme = "https";
        if (srv.Any(x => x != null && x.Contains("localhost")))
            scheme = "http";
        var url = $"{scheme}://{srv}/{httpReq.Headers["X-Forwarded-Prefix"]}";
        document.Servers.Add(new OpenApiServer { Url = url });
    }

    /// <summary>
    /// Reads one downstream OpenAPI document (3.0, 3.1 or 3.2). Returns null, and logs, when it does not
    /// load cleanly, so one bad document never blocks the others from merging.
    /// </summary>
    internal static async Task<OpenApiDocument?> LoadDownstreamAsync(string json, string? source, ILogger logger)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        Microsoft.OpenApi.Reader.ReadResult loadResult;
        try
        {
            loadResult = await OpenApiDocument.LoadAsync(stream);
        }
        catch (Exception ex)
        {
            // An unsupported "openapi" version (OpenApiUnsupportedSpecVersionException) throws rather
            // than reporting a diagnostic; malformed input can throw too.
            if (logger.IsEnabled(LogLevel.Error))
                logger.LogError(ex, "Error with OpenAPI document from {Url} {Message}", source, ex.Message);
            return null;
        }

        if (loadResult == null || loadResult.Diagnostic == null || loadResult.Diagnostic.Errors.Count > 0)
        {
            if (logger.IsEnabled(LogLevel.Error))
                logger.LogError("Error with OpenAPI document from {Url} {Message}", source, loadResult?.Diagnostic?.Errors?.FirstOrDefault()?.Message);
            return null;
        }

        return loadResult.Document;
    }

    /// <summary>
    /// Merges downstream documents into <paramref name="document"/>: schemas the target lacks are added, and
    /// paths matching a route are added under the route's client-facing ApiUrl. Null documents are skipped.
    /// </summary>
    internal static void Merge(OpenApiDocument document, IEnumerable<OpenApiDocument?> downstreamDocs, ProxyConfig[] config)
    {
        foreach (var downstreamDoc in downstreamDocs)
        {
            if (downstreamDoc == null || downstreamDoc.Components == null || downstreamDoc.Paths == null)
                continue;

            // Merge schemas
            if (downstreamDoc.Components.Schemas != null)
            {
                document.Components ??= new OpenApiComponents();
                document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
                foreach (var schema in downstreamDoc.Components.Schemas)
                {
                    if (!document.Components.Schemas.ContainsKey(schema.Key))
                        document.Components.Schemas[schema.Key] = schema.Value;
                }
            }

            // Merge paths
            foreach (var path in downstreamDoc.Paths)
            {
                if (path.Value == null)
                    continue;
                // Match downstream path to proxyConfig:
                // Downstream services expose ProxyUrl paths; rewrite them to ApiUrl (the client-facing path).
                // Fallback: if path already matches ApiUrl (service uses client-facing path directly), no rewrite needed.
                var proxyConfig = config.FirstOrDefault(x => path.Key.Replace(@"/", string.Empty) == x.ProxyUrl.Replace(@"/", string.Empty));
                string? proxyUrlToRewrite = proxyConfig?.ProxyUrl;

                proxyConfig ??= config.FirstOrDefault(x => path.Key.Replace(@"/", string.Empty) == (x.ApiUrl ?? x.ProxyUrl).Replace(@"/", string.Empty));

                if (proxyConfig != null)
                {
                    if (path.Value.Operations != null)
                        foreach (var operation in path.Value.Operations)
                        {
                            if (operation.Value.Tags != null)
                                SetTagsToApiUrl(document, proxyConfig, operation);
                        }

                    var proxyApi = path.Key;
                    if (proxyUrlToRewrite != null && !string.IsNullOrEmpty(proxyConfig.ApiUrl))
                        proxyApi = path.Key.Replace(proxyUrlToRewrite, proxyConfig.ApiUrl);

                    document.Paths[proxyApi] = path.Value;
                }

                RelaxCompulsoryArrayParameters(path);
            }
        }
    }

    private static void SetTagsToApiUrl(OpenApiDocument document, ProxyConfig proxyConfig, KeyValuePair<HttpMethod, OpenApiOperation> operation)
    {
        // Replace the downstream service's own tags with one derived from the client-facing path,
        // so the merged document groups operations the way callers see them.
        operation.Value.Tags!.Clear();

        // Take the second path segment (the one after the version) when there is one.
        var apiUrl = proxyConfig.ApiUrl ?? proxyConfig.ProxyUrl ?? string.Empty;
        var segments = apiUrl.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        string rawTag = segments.Length >= 2
            ? segments[1]
            : (segments.Length == 1 ? segments[0] : "API");

        // Normalize tag: replace dashes with spaces and Title Case
        var normalized = rawTag.Replace("-", " ");
        var tagName = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(normalized);
        operation.Value.Tags?.Add(new OpenApiTagReference(tagName, document, null));
    }

    /// <summary>
    /// Works around an OpenAPI generation quirk where string-array query parameters are emitted as
    /// required. Rebuilds them as optional form-style, non-exploded parameters so "a,b,c" renders
    /// correctly and callers may omit them.
    /// </summary>
    private static void RelaxCompulsoryArrayParameters(KeyValuePair<string, IOpenApiPathItem> path)
    {
        if (path.Value?.Operations == null)
            return;

        foreach (var opEntry in path.Value.Operations)
        {
            var operation = opEntry.Value;
            var parameters = operation?.Parameters;
            if (parameters == null || parameters.Count == 0)
                continue;

            for (int i = 0; i < parameters.Count; i++)
            {
                var par = parameters[i];
                if (par == null)
                    continue;
                var s = par.Schema;
                if (s != null &&
                    s.Type == JsonSchemaType.Array &&
                    s.Items != null &&
                    s.Items.Type == JsonSchemaType.String)
                {
                    parameters[i] = new OpenApiParameter
                    {
                        Name = par.Name,
                        In = par.In,
                        Description = par.Description,
                        Required = false,
                        Schema = new OpenApiSchema { Type = s.Type, Items = s.Items },
                        Style = ParameterStyle.Form,
                        Explode = false
                    };
                }
            }
        }
    }
}
