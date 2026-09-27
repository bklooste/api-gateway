namespace ApiGateway;

/// <summary>
/// Gateway-wide settings, bound from the <c>gateway</c> configuration section.
/// Every value has a working default, so the section may be omitted entirely.
/// </summary>
public sealed class GatewayOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "gateway";

    /// <summary>Names of the identity headers the authenticating proxy sets on inbound requests.</summary>
    public GatewayHeaderNames Headers { get; init; } = new();

    /// <summary>How long a loaded <c>proxyConfig</c> is held before being re-read. Zero disables reloading.</summary>
    public int ConfigReloadMinutes { get; init; } = 10;

    /// <summary>Timeout for the <see cref="ResourceAuth"/> lookup, so a slow downstream cannot stall the request.</summary>
    public int ResourceAuthTimeoutSeconds { get; init; } = 5;

    /// <summary>Timeout for fetching each proxied service's OpenAPI document during the merge.</summary>
    public int OpenApiFetchTimeoutSeconds { get; init; } = 3;

    /// <summary>
    /// Maps the <c>debug</c> endpoint, which echoes the caller's identity headers back. Off by default:
    /// it discloses the trusted headers and belongs behind an explicit opt-in, never on in production.
    /// </summary>
    public bool EnableDebugEndpoint { get; init; }

    /// <summary>
    /// The gateway always sets the <c>userId</c> query parameter from the user-id header, and by default the
    /// <c>brand</c> query parameter from the brand header, overwriting any the caller sent. Turn this off where
    /// the caller legitimately chooses the brand (an admin gateway) and every route that uses it authorizes it.
    /// </summary>
    public bool OverwriteBrandQueryParam { get; init; } = true;
}

/// <summary>
/// Names of the identity headers set by the authenticating proxy in front of the gateway
/// (Envoy ext_authz in the reference deployment) and consumed by the gateway.
/// </summary>
public sealed class GatewayHeaderNames
{
    /// <summary>Header carrying the caller's comma-separated granted scopes.</summary>
    public string Scopes { get; init; } = "auth-claim-scopes";

    /// <summary>Header carrying the caller's user id.</summary>
    public string UserId { get; init; } = "userid";

    /// <summary>Header carrying the caller's brand/tenant.</summary>
    public string Brand { get; init; } = "brand";

    /// <summary>
    /// Fallback header names for the <c>{brand}</c> scope placeholder, consulted only when
    /// <see cref="Brand"/> is absent.
    ///
    /// Empty by default, and it should usually stay that way. Anything listed here is trusted for an
    /// authorization decision, so it must be a header the authenticating proxy sets and overwrites.
    /// `Orange.Lib.Gateway` defaulted this to <c>X-Brand</c> and preferred it over <see cref="Brand"/>,
    /// which let a caller choose the brand a <c>…:b={brand}</c> predicate was evaluated against —
    /// see the README.
    /// </summary>
    public string[] BrandAlternates { get; init; } = [];

    /// <summary>
    /// Identity headers forwarded to downstream services verbatim rather than through the generic
    /// header copy. Copying them generically would join duplicate values with ", " and corrupt
    /// exact-match lookups downstream. <see cref="Scopes"/>, <see cref="UserId"/> and
    /// <see cref="Brand"/> are always included.
    /// </summary>
    public string[] ForwardedIdentityHeaders { get; init; } = ["usertype", "auth-claim-brand"];

    private IReadOnlySet<string>? _allIdentityHeaders;

    /// <summary>
    /// The full set of identity headers, forwarded explicitly and excluded from the generic copy.
    /// Built once: the properties are init-only, and this is read on every request.
    /// </summary>
    public IReadOnlySet<string> AllIdentityHeaders() =>
        _allIdentityHeaders ??= new HashSet<string>(
            ForwardedIdentityHeaders.Concat([Scopes, UserId, Brand]).Concat(BrandAlternates),
            StringComparer.OrdinalIgnoreCase);
}
