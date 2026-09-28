namespace ApiGateway;

/// <summary>
/// One route of the config-driven gateway, bound from the <c>proxyConfig</c> array.
/// A request whose path matches <see cref="ApiUrl"/> is forwarded to the <c>EndPoints:{ProxyName}</c>
/// service at <see cref="ProxyUrl"/>. Prefix changes are not supported (swagger would need remapping).
/// </summary>
public record ProxyConfig
{
    /// <summary>Name of the downstream service; its base URL is <c>EndPoints:{ProxyName}</c>.</summary>
    public string ProxyName { get; init; } = string.Empty;

    /// <summary>Downstream path template; <c>{param}</c> segments are filled from <see cref="ApiUrl"/> and <c>{userId}</c> from the user-id header.</summary>
    public string ProxyUrl { get; init; } = string.Empty;

    /// <summary>Client-facing path template. Matches as a prefix; <c>{param}</c> segments are wildcards.</summary>
    public string ApiUrl { get; init; } = string.Empty;

    /// <summary>Optional HTTP method (case-insensitive). Null matches any method.</summary>
    public string? Method { get; init; }

    /// <summary>
    /// The caller needs at least one of these scopes. Placeholders: <c>{userId}</c>, <c>{brand}</c>,
    /// <c>{path:name}</c>, <c>{query:name}</c> and <c>{resource:Prop}</c>.
    /// </summary>
    public string[]? AnyScopesPredicate { get; init; }

    /// <summary>
    /// The caller needs every one of these scopes (same placeholders as <see cref="AnyScopesPredicate"/>).
    /// An entry may offer alternatives as <c>"a|b"</c>; see <see cref="AuthorizationHelper.All"/>.
    /// </summary>
    public string[]? AllScopesPredicate { get; init; }

    /// <summary>When set, only this top-level property of a successful JSON response is returned.</summary>
    public string? ResponsePath { get; init; }

    /// <summary>Resolves <c>{resource:Prop}</c> scope placeholders by fetching the target resource first.</summary>
    public ResourceAuth? ResourceAuth { get; init; }

    /// <summary>When set, the request is routed through the cache service (GET only unless <see cref="Method"/> is pinned).</summary>
    public CacheConfig? Cache { get; init; }

    // ---- Precomputed at load time by HttpMessageLogic.LoadProxyConfig ----
    //
    // These are derived, not bound from configuration. They exist so the per-request path can skip
    // work that most routes never need: in the two real route tables only 11/70 and 3/135 routes use
    // any scope placeholder, and none use {resource:}. Recomputing that per request cost roughly
    // twenty allocations to reach a no-op.

    /// <summary>True when this route declares any scope predicate. False means authorization is a no-op.</summary>
    public bool HasPredicates { get; init; }

    /// <summary>True when any predicate contains a <c>{...}</c> placeholder needing substitution.</summary>
    public bool HasPlaceholders { get; init; }

    /// <summary>True when any predicate contains a <c>{resource:...}</c> placeholder needing a lookup.</summary>
    public bool NeedsResourceLookup { get; init; }

    /// <summary>Derives the precomputed flags. Must run *after* globalRequiredScopes are merged in.</summary>
    internal ProxyConfig Compile()
    {
        var all = (AnyScopesPredicate ?? []).Concat(AllScopesPredicate ?? []).ToArray();
        return this with
        {
            HasPredicates = all.Length > 0,
            HasPlaceholders = all.Any(p => p.Contains('{')),
            NeedsResourceLookup = ResourceAuth != null && all.Any(p => p.Contains("{resource:", StringComparison.Ordinal))
        };
    }
}

/// <summary>
/// Cache keying for routes proxied via the cache service: the query params and headers that form
/// the cache key, plus the entry TTL.
/// </summary>
public record CacheConfig
{
    /// <summary>Query-string parameters whose values form part of the cache key.</summary>
    public string[]? CacheVars { get; init; }

    /// <summary>Request headers whose values form part of the cache key.</summary>
    public string[]? CacheHeaders { get; init; }

    /// <summary>Entry lifetime in seconds. Null lets the cache service apply its own default.</summary>
    public int? TtlSeconds { get; init; }
}

/// <summary>
/// Resource lookup backing <c>{resource:Prop}</c> scope placeholders. Before the scope check, the
/// gateway fetches <see cref="Url"/> from <see cref="Service"/> and reads <see cref="Property"/> off
/// the JSON response, e.g. <c>Url = "state/{betId}"</c> with <c>Property = "Brand"</c> resolves
/// <c>{resource:Brand}</c>.
/// </summary>
public record ResourceAuth
{
    /// <summary>Name of the service to fetch from; its base URL is <c>EndPoints:{Service}</c>.</summary>
    public string Service { get; init; } = string.Empty;

    /// <summary>Path template on that service. <c>{param}</c> segments are filled from the request path.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>Top-level JSON property read off the response and substituted into the scope predicate.</summary>
    public string Property { get; init; } = "Brand";
}
