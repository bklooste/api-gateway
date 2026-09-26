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
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
                var loadResult = await OpenApiDocument.LoadAsync(stream);

                if (loadResult == null || loadResult.Diagnostic == null || loadResult.Diagnostic.Errors.Count > 0)
                {
                    if (logger.IsEnabled(LogLevel.Error))
                        logger.LogError("Error with OpenAPI document from {Url} {Message}", x.Value, loadResult?.Diagnostic?.Errors?.FirstOrDefault()?.Message);
                    return (x.Key, Downstream: (OpenApiDocument?)null);
                }

                return (x.Key, Downstream: (OpenApiDocument?)loadResult.Document);
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Error))
                    logger.LogError(ex, "Error getting OpenAPI document {Message}", ex.Message);
                return (x.Key, Downstream: (OpenApiDocument?)null);
            }
        });

        var results = await Task.WhenAll(tasks);

        foreach (var result in results)
        {
            var downstreamDoc = result.Downstream;

            if (downstreamDoc == null || downstreamDoc.Components == null || downstreamDoc.Paths == null)
                continue;

            // Merge schemas
            if (downstreamDoc.Components.Schemas != null)
                foreach (var schema in downstreamDoc.Components.Schemas)
                {
                    if (!document.Components!.Schemas!.ContainsKey(schema.Key))
                        document.Components.Schemas[schema.Key] = schema.Value;
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
