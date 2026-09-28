using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace ApiGateway;

/// <summary>
/// The config-driven gateway: matches a request against <c>proxyConfig</c>, checks scopes, rewrites the URL
/// and forwards it (optionally through the cache service).
/// </summary>
public class HttpMessageLogic
{
    private readonly Proxy proxy;
    private readonly ILogger logger;
    private readonly RouteTableProvider _routes;
    private readonly GatewayOptions _options;

    public HttpMessageLogic(Proxy proxy, ILoggerFactory loggerFactory, RouteTableProvider routes, IOptions<GatewayOptions> options)
    {
        this.proxy = proxy;
        logger = loggerFactory.CreateLogger<HttpMessageLogic>();
        _routes = routes;
        _options = options.Value;
    }

    /// <summary>
    /// The active route table. Rebuilding happens in <see cref="RouteTableProvider"/>, off the
    /// request path, so this is only ever a reference read.
    /// </summary>
    private ProxyConfig[] Config => _routes.Current;

    /// <summary>
    /// Binds <c>proxyConfig</c> and merges the service-wide <c>globalRequiredScopes</c> (e.g. <c>["admin"]</c> for
    /// an admin-facing instance) into every entry's <see cref="ProxyConfig.AllScopesPredicate"/>.
    /// </summary>
    public static ProxyConfig[] LoadProxyConfig(IConfiguration configuration)
    {
        var raw = configuration.GetSection("proxyConfig").Get<ProxyConfig[]>() ?? throw new ArgumentException("invalid config");
        var globalRequired = configuration.GetSection("globalRequiredScopes").Get<string[]>();

        // Compile() must run after the global merge: globalRequiredScopes can be the only predicate a
        // route has, so deriving the flags from the raw config would mark it as needing no auth.
        if (globalRequired is not { Length: > 0 })
            return [.. raw.Select(c => c.Compile())];

        return [.. raw.Select(c => (c with
        {
            AllScopesPredicate = [.. (c.AllScopesPredicate ?? []).Concat(globalRequired).Distinct(StringComparer.Ordinal)]
        }).Compile())];
    }

    /// <summary>Handles one gateway request for <paramref name="slug"/> (the path without leading slash).</summary>
    public async Task<IResult> ProcessCall(HttpRequest request, string slug)
    {
        var ct = request.HttpContext.RequestAborted;
        var headers = _options.Headers;
        request.Headers.TryGetValue(headers.Scopes, out var scopes);
        request.Headers.TryGetValue(headers.Brand, out var brand);
        request.Headers.TryGetValue(headers.UserId, out var userId);

        // Sort descending by template segment count so the most-specific entry wins when
        // a shorter template is also a prefix of the path (e.g. v1/me/wallet vs v1/me/wallet/withdraw/balance).
        var pathMatches = Config?.Where(c => PathMatchesTemplate(request.Path, c.ApiUrl))
            .OrderByDescending(c => c.ApiUrl.Count(ch => ch == '/'))
            .ToList();

        if (pathMatches == null || pathMatches.Count == 0)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning("No config entry found for path {Path}", request.Path);
            return Results.NotFound("not found");
        }

        // Prefer an entry whose Method matches the request; fall back to method-agnostic entries.
        var configEntry = pathMatches.FirstOrDefault(c => c.Method != null && string.Equals(c.Method, request.Method, StringComparison.OrdinalIgnoreCase))
            ?? pathMatches.FirstOrDefault(c => c.Method == null);

        if (configEntry == null)
        {
            // URL matched at least one entry but none accepted this method — hard reject.
            if (logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning("Method {Method} not allowed for path {Path}", request.Method, request.Path);
            return Results.BadRequest($"Method {request.Method} not allowed for path {request.Path}");
        }

        if (!await IsAuthValid(scopes, configEntry, userId, proxy, request, ct))
        {
            logger.LogWarning("Auth failed {Path} {Scopes} {UserId}", request.Path, scopes, userId);
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        // Cache is opt-in per route. When a route doesn't pin Method, only GET requests
        // route through the cache — the same path may also serve PUT/POST/DELETE writes which
        // must bypass it.
        var useCache = configEntry.Cache != null
            && (configEntry.Method != null
                ? string.Equals(configEntry.Method, request.Method, StringComparison.OrdinalIgnoreCase)
                : HttpMethods.IsGet(request.Method));
        var proxyClient = useCache
            ? proxy.GetHttpClient(CacheServiceName)
            : proxy.GetHttpClient(configEntry.ProxyName);
        // Comma-separated values are exploded into repeated keys so the downstream string[] binder works.
        var queryParams = QueryStrings.GetQueryString(request, brand, userId, _options.OverwriteBrandQueryParam);
        var url = QueryStrings.AddQueryParams(slug, queryParams);
        url = RewriteUrl(url, configEntry.ApiUrl, configEntry.ProxyUrl, userId.ToString(), brand.ToString());

        if (useCache)
        {
            var upstreamBase = proxy.GetEndpointUrl(configEntry.ProxyName)
                ?? throw new InvalidOperationException($"No EndPoint registered for ProxyName '{configEntry.ProxyName}'");
            url = BuildCacheUrl(upstreamBase, url, configEntry.Cache!);
        }

        var newRequest = await CopyRequest(request, url, headers);

        // ResponseHeadersRead: return as soon as the status and headers arrive instead of buffering
        // the whole body first. Without it the Results.Stream path below is not streaming at all —
        // it copies an already fully-buffered body, so a large payload is held in memory in full and
        // time-to-first-byte waits for the complete downstream response.
        //
        // RequestAborted: a client that disconnects must not leave the downstream call running to
        // HttpClient's default 100s timeout, holding a connection open the whole time.
        var response = await proxyClient.SendAsync(newRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        // The response outlives this method on the streaming path, so ownership passes to the
        // request: it is disposed once the response has been written.
        request.HttpContext.Response.RegisterForDispose(response);

        var contentType = response.Content.Headers.ContentType?.ToString() ?? "text/plain";

        // File downloads carry their name here; without it the client saves an unnamed file.
        if (response.Content.Headers.ContentDisposition is { } disposition)
            request.HttpContext.Response.Headers.ContentDisposition = disposition.ToString();

        if (response.IsSuccessStatusCode)
        {
            if (!string.IsNullOrEmpty(configEntry.ResponsePath))
            {
                // This branch has to buffer: it reshapes the body, so it needs all of it.
                var bodyBytes = await response.Content.ReadAsByteArrayAsync(ct);
                using var docResponse = JsonDocument.Parse(bodyBytes);
                if (docResponse.RootElement.TryGetProperty(configEntry.ResponsePath, out var subElement))
                {
                    // Clone before the JsonDocument is disposed — Results.Json serializes asynchronously
                    // after this method returns, by which point the using block would have freed the backing memory.
                    return Results.Json(subElement.Clone(), GatewayJson.IgnoreDefault);
                }
                return Results.Bytes(bodyBytes, contentType);
            }

            return Results.Stream(await response.Content.ReadAsStreamAsync(ct), contentType);
        }

        return Results.Content(
            await response.Content.ReadAsStringAsync(ct),
            contentType: contentType,
            statusCode: (int)response.StatusCode
        );
    }

    /// <summary>
    /// <c>EndPoints</c> key of the cache service routes opt into via <see cref="CacheConfig"/>.
    /// The reference deployment points this at http-rediscache.
    /// </summary>
    public const string CacheServiceName = "cache";

    /// <summary>
    /// Evaluates the entry's scope predicates against the caller's comma-separated scopes.
    ///
    /// Structured around the precomputed flags on <see cref="ProxyConfig"/> so the common cases cost
    /// almost nothing: a route with no predicates returns immediately without touching the scopes
    /// header, and a route whose predicates contain no placeholders skips substitution entirely and
    /// matches the stored pattern array directly.
    /// </summary>
    public async Task<bool> IsAuthValid(string? scopesHeader, ProxyConfig configEntry, StringValues userId, Proxy proxy, HttpRequest request, CancellationToken ct = default)
    {
        // Nothing to check — don't split the header or build a set.
        if (!configEntry.HasPredicates)
            return true;

        var scopes = scopesHeader?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];

        var authHelper = new AuthorizationHelper(scopes);

        // Fixed patterns: compare as-is, no context dictionary and no per-pattern string.Replace.
        if (!configEntry.HasPlaceholders)
        {
            return (configEntry.AnyScopesPredicate is null || authHelper.Any(configEntry.AnyScopesPredicate))
                && (configEntry.AllScopesPredicate is null || authHelper.All(configEntry.AllScopesPredicate));
        }

        var headers = _options.Headers;
        // The proxy-set Brand header wins. Alternates are fallbacks only, never overrides: preferring
        // an alternate would let a caller pick the brand its own {brand} predicate is checked against.
        string? brand = request.Headers[headers.Brand].FirstOrDefault();
        if (string.IsNullOrEmpty(brand))
        {
            foreach (var alternate in headers.BrandAlternates)
            {
                brand = request.Headers[alternate].FirstOrDefault();
                if (!string.IsNullOrEmpty(brand))
                    break;
            }
        }

        string? resourceValue = null;
        string? resourceKey = null;
        if (configEntry.NeedsResourceLookup)
        {
            resourceValue = await GetResourceAttributeAsync(proxy, configEntry.ResourceAuth!, request.Path, configEntry.ApiUrl, ct);
            if (!string.IsNullOrEmpty(resourceValue))
                resourceKey = "{resource:" + configEntry.ResourceAuth!.Property + "}";
        }

        string Substitute(string pattern)
        {
            // string.Replace returns the same instance when there is no match, so a pattern that
            // uses only some of the placeholders allocates only for the ones it actually contains.
            var r = pattern;
            if (userId.Count > 0)
            {
                var id = userId.ToString();
                r = r.Replace("{userId}", id, StringComparison.OrdinalIgnoreCase);
                r = r.Replace("{0}", id, StringComparison.Ordinal);
            }
            if (!string.IsNullOrEmpty(brand))
                r = r.Replace("{brand}", brand, StringComparison.OrdinalIgnoreCase);
            if (resourceKey != null)
                r = r.Replace(resourceKey, resourceValue, StringComparison.OrdinalIgnoreCase);
            if (r.Contains("{path:", StringComparison.OrdinalIgnoreCase))
                r = SubstitutePathParameters(r, request.Path, configEntry.ApiUrl);
            if (r.Contains("{query:", StringComparison.OrdinalIgnoreCase))
                r = SubstituteQueryParameters(r, request.Query);
            return r;
        }

        static string[] Map(string[] patterns, Func<string, string> f)
        {
            var result = new string[patterns.Length];
            for (int i = 0; i < patterns.Length; i++)
                result[i] = f(patterns[i]);
            return result;
        }

        if (configEntry.AnyScopesPredicate is { } anyPatterns && !authHelper.Any(Map(anyPatterns, Substitute)))
            return false;

        if (configEntry.AllScopesPredicate is { } allPatterns && !authHelper.All(Map(allPatterns, Substitute)))
            return false;

        return true;
    }

    /// <summary>
    /// Replaces <c>{path:name}</c> in a scope pattern with the request's value for the <c>{name}</c> segment of
    /// <paramref name="apiUrlTemplate"/>, e.g. <c>brand-r:{path:brand}</c> on <c>v1/admin/brand/{brand}</c>.
    /// Lets a route gate on the resource named in the URL (the brand being administered) rather than the caller's own.
    /// An unknown name is left as-is, so the predicate cannot match by accident.
    /// </summary>
    public static string SubstitutePathParameters(string pattern, string requestPath, string apiUrlTemplate)
    {
        var pathSegments = requestPath.TrimStart('/').Split('/');
        var templateSegments = apiUrlTemplate.TrimStart('/').Split('/');
        for (int i = 0; i < Math.Min(templateSegments.Length, pathSegments.Length); i++)
        {
            var tmpl = templateSegments[i];
            if (tmpl.StartsWith('{') && tmpl.EndsWith('}'))
                pattern = pattern.Replace("{path:" + tmpl[1..^1] + "}", Uri.UnescapeDataString(pathSegments[i]), StringComparison.OrdinalIgnoreCase);
        }
        return pattern;
    }

    /// <summary>
    /// Replaces <c>{query:name}</c> in a scope pattern with the request's <c>?name=</c> value, e.g.
    /// <c>brand-r:{query:brand}</c> where the brand being administered travels in the query string rather than
    /// the path. Same job as <see cref="SubstitutePathParameters"/>, and safe for the same reason: the value is
    /// the one the downstream service acts on, so the check and the effect read a single input.
    ///
    /// A missing or empty value is left as-is, as is a parameter supplied <b>more than once</b> — which repeat a
    /// service binds is its own business, so neither can be checked on its behalf. An unresolved placeholder
    /// keeps its braces and so cannot match a granted scope; it must never collapse to an empty string, because
    /// a bare <c>brand-r:</c> scopes nothing and a caller can hold it.
    /// </summary>
    public static string SubstituteQueryParameters(string pattern, IQueryCollection query)
    {
        const string open = "{query:";
        var from = 0;
        while (true)
        {
            var start = pattern.IndexOf(open, from, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                break;
            var nameStart = start + open.Length;
            var end = pattern.IndexOf('}', nameStart);
            if (end < 0)
                break;

            var values = query[pattern[nameStart..end]];
            if (values.Count == 1 && !string.IsNullOrEmpty(values[0]))
            {
                var value = values[0]!;
                pattern = pattern.Replace(pattern[start..(end + 1)], value, StringComparison.OrdinalIgnoreCase);
                from = start + value.Length;
            }
            else
            {
                from = end + 1;
            }
        }
        return pattern;
    }

    private async Task<string?> GetResourceAttributeAsync(Proxy proxy, ResourceAuth resourceAuth, string requestPath, string apiUrl, CancellationToken ct = default)
    {
        try
        {
            // 1. Extract IDs from the request path based on the apiUrl template
            // e.g. apiUrl: v1/admin/bet/{betId}/cancel, requestPath: /v1/admin/bet/123/cancel
            // We simplify this by assuming {id} style placeholders
            var segments = apiUrl.Split('/');
            var pathSegments = requestPath.TrimStart('/').Split('/');
            var replacements = new Dictionary<string, string>();

            for (int i = 0; i < Math.Min(segments.Length, pathSegments.Length); i++)
            {
                if (segments[i].StartsWith('{') && segments[i].EndsWith('}'))
                {
                    var key = segments[i].Trim('{', '}');
                    replacements[key] = pathSegments[i];
                }
            }

            // 2. Build the resource fetch URL
            var fetchUrl = resourceAuth.Url;
            foreach (var (key, value) in replacements)
            {
                fetchUrl = fetchUrl.Replace($"{{{key}}}", value);
            }

            // 3. Fetch the resource — short timeout so a slow downstream doesn't block the whole request
            var client = proxy.GetHttpClient(resourceAuth.Service);
            // Linked, so the lookup is abandoned when the client disconnects, not only on timeout.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.ResourceAuthTimeoutSeconds));
            var response = await client.GetAsync(fetchUrl, cts.Token);
            if (!response.IsSuccessStatusCode)
                return null;

            // 4. Extract the property
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cts.Token), cancellationToken: cts.Token);
            if (doc.RootElement.TryGetProperty(resourceAuth.Property, out var prop))
            {
                return prop.GetString();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during ResourceAuth fetch for {Path}", requestPath);
        }
        return null;
    }

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Proxy-Connection", "Keep-Alive", "Transfer-Encoding", "TE", "Trailer", "Upgrade"
    };

    private static readonly HashSet<string> ContentHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Length", "Content-Language", "Content-Location",
        "Content-MD5", "Content-Range", "Content-Disposition", "Content-Encoding"
    };

    /// <summary>
    /// Rewrites a request URL by replacing the ApiUrl template prefix with the ProxyUrl template,
    /// substituting path parameter values extracted from the actual URL segments.
    /// e.g. url="v1/admin/bet/abc/cancel?q=1", apiUrl="v1/admin/bet/{betId}/cancel", proxyUrl="v1/bet/{betId}/cancel"
    ///      → "v1/bet/abc/cancel?q=1"
    /// Falls back to simple string replacement for non-parameterized routes.
    /// </summary>
    public static string RewriteUrl(string url, string apiUrlTemplate, string proxyUrlTemplate, string? userId = null, string? brand = null)
    {
        // Split off query string
        var qIndex = url.IndexOf('?');
        var pathPart = qIndex >= 0 ? url[..qIndex] : url;
        var queryPart = qIndex >= 0 ? url[qIndex..] : string.Empty;

        var pathSegments = pathPart.TrimStart('/').Split('/');
        var templateSegments = apiUrlTemplate.TrimStart('/').Split('/');

        // Extract param values from path
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Math.Min(templateSegments.Length, pathSegments.Length); i++)
        {
            var tmpl = templateSegments[i];
            if (tmpl.StartsWith('{') && tmpl.EndsWith('}'))
                replacements[tmpl.Trim('{', '}')] = pathSegments[i];
        }

        // Build the rewritten path: substitute param values into ProxyUrl, then append any trailing segments
        var proxySegments = proxyUrlTemplate.TrimStart('/').Split('/');
        var rewritten = string.Join('/', proxySegments.Select(s =>
            s.StartsWith('{') && s.EndsWith('}') && replacements.TryGetValue(s.Trim('{', '}'), out var v) ? v : s));

        // Append extra path segments beyond the template length (trailing slug)
        if (pathSegments.Length > templateSegments.Length)
            rewritten += "/" + string.Join('/', pathSegments[templateSegments.Length..]);

        // Substitute {userId} from the identity header (enables e.g. ProxyUrl: "v1/user/{userId}/settings")
        if (!string.IsNullOrEmpty(userId))
            rewritten = rewritten.Replace("{userId}", Uri.EscapeDataString(userId));

        // Same for {brand} (e.g. ProxyUrl: "v1/brand/{brand}"). A {brand} path parameter in ApiUrl was already
        // substituted above, so this only fills a ProxyUrl-only {brand} from the trusted header.
        if (!string.IsNullOrEmpty(brand))
            rewritten = rewritten.Replace("{brand}", Uri.EscapeDataString(brand), StringComparison.OrdinalIgnoreCase);

        return rewritten + queryPart;
    }

    /// <summary>
    /// Matches a request path against a config ApiUrl template.
    /// Template segments of the form {param} act as wildcards.
    /// The template must match a prefix of the path (remaining segments are allowed).
    /// </summary>
    public static bool PathMatchesTemplate(string requestPath, string apiUrlTemplate)
    {
        var pathSegments = requestPath.TrimStart('/').Split('/', StringSplitOptions.None);
        var templateSegments = apiUrlTemplate.TrimStart('/').Split('/', StringSplitOptions.None);

        if (pathSegments.Length < templateSegments.Length)
            return false;

        for (int i = 0; i < templateSegments.Length; i++)
        {
            var tmpl = templateSegments[i];
            if (tmpl.StartsWith('{') && tmpl.EndsWith('}'))
                continue; // wildcard — matches any segment value
            if (!string.Equals(pathSegments[i], tmpl, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Build the relative URL used to call the cache service:
    /// <c>cache?cacheVar=...&amp;cacheHeader=...&amp;url=&lt;upstream-host&gt;/&lt;rewrittenUrl&gt;</c>.
    /// The cache client's BaseAddress is prepended by HttpClient itself.
    /// </summary>
    public static string BuildCacheUrl(string upstreamBaseUrl, string rewrittenUrl, CacheConfig cache)
    {
        var trimmedBase = upstreamBaseUrl.TrimEnd('/');
        var trimmedPath = rewrittenUrl.TrimStart('/');
        var fullUpstream = $"{trimmedBase}/{trimmedPath}";

        var sb = new System.Text.StringBuilder("cache?");
        if (cache.CacheVars != null)
        {
            foreach (var v in cache.CacheVars)
            {
                sb.Append("cacheVar=").Append(Uri.EscapeDataString(v)).Append('&');
            }
        }
        if (cache.CacheHeaders != null)
        {
            foreach (var h in cache.CacheHeaders)
            {
                sb.Append("cacheHeader=").Append(Uri.EscapeDataString(h)).Append('&');
            }
        }
        sb.Append("url=").Append(Uri.EscapeDataString(fullUpstream));
        if (cache.TtlSeconds.HasValue)
            sb.Append("&ttl=").Append(cache.TtlSeconds.Value);
        return sb.ToString();
    }

    private static bool MethodAllowsBody(string method)
        => HttpMethod.Post.Method.Equals(method, StringComparison.OrdinalIgnoreCase)
        || HttpMethod.Put.Method.Equals(method, StringComparison.OrdinalIgnoreCase)
        || HttpMethod.Patch.Method.Equals(method, StringComparison.OrdinalIgnoreCase)
        || HttpMethod.Delete.Method.Equals(method, StringComparison.OrdinalIgnoreCase);

    private static async Task<HttpRequestMessage> CopyRequest(HttpRequest source, string baseUrl, GatewayHeaderNames headers)
    {
        var target = new HttpRequestMessage(new HttpMethod(source.Method), baseUrl);

        if (source.Protocol.Equals("HTTP/2", StringComparison.OrdinalIgnoreCase))
            target.Version = HttpVersion.Version20;
        else if (source.Protocol.Equals("HTTP/1.1", StringComparison.OrdinalIgnoreCase))
            target.Version = HttpVersion.Version11;

        // ---- Body ----
        if (MethodAllowsBody(source.Method))
        {
            // Some clients send chunked bodies (ContentLength == null). We still copy if readable.
            source.EnableBuffering();
            source.Body.Position = 0;

            // Buffer once into a byte[] so HttpClient owns the payload (prevents ObjectDisposedException).
            await using var ms = new MemoryStream();
            await source.Body.CopyToAsync(ms, source.HttpContext.RequestAborted);
            var buffer = ms.ToArray();

            var content = new ByteArrayContent(buffer);

            // Preserve Content-Type (including boundary for multipart/form-data)
            if (!string.IsNullOrEmpty(source.ContentType))
                content.Headers.TryAddWithoutValidation("Content-Type", source.ContentType);

            target.Content = content;
        }

        // Identity headers are forwarded explicitly below — skip them here to prevent duplication.
        // Duplicate values on custom headers are joined by ", " in ASP.NET Core, which breaks
        // exact-match lookups downstream (e.g. a brand allow-list Contains check).
        var identityHeaders = headers.AllIdentityHeaders();

        foreach (var header in source.Headers)
        {
            var name = header.Key;

            if (HopByHopHeaders.Contains(name))
                continue;

            if (ContentHeaderNames.Contains(name))
                continue;

            if (identityHeaders.Contains(name))
                continue;

            if (!target.Headers.TryAddWithoutValidation(name, (IEnumerable<string>)header.Value))
                target.Content?.Headers.TryAddWithoutValidation(name, (IEnumerable<string>)header.Value);
        }

        // Re-add the identity headers as a single value each. These are passed through as received:
        // the gateway does not strip or re-sign them, so the authenticating proxy in front must
        // overwrite rather than append, or a client's own value survives alongside the trusted one.
        foreach (var h in identityHeaders)
        {
            if (source.Headers.TryGetValue(h, out var v) && !string.IsNullOrEmpty(v))
                target.Headers.TryAddWithoutValidation(h, (string?)v);
        }

        return target;
    }
}
