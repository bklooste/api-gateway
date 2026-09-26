namespace ApiGateway;

/// <summary>
/// Scope checks against the caller's granted scopes.
///
/// There is deliberately no local-development bypass. The gateway's "credentials" are plaintext
/// identity headers, so debugging against it means sending the scopes you want — a bypass would
/// buy nothing and would add a mode in which authorization silently does not run.
/// </summary>
public sealed class AuthorizationHelper(IEnumerable<string>? scopes)
{
    private readonly HashSet<string> scopes = [.. scopes ?? []];

    /// <summary>True when no scopes are required or the caller holds at least one.</summary>
    public bool Any(params string[] reqScopes) => reqScopes.Length == 0 || reqScopes.Any(Contains);

    /// <summary>True when no scopes are required or the caller holds all of them.</summary>
    public bool All(params string[] reqScopes) => reqScopes.Length == 0 || reqScopes.All(Contains);

    /// <summary>True when the caller holds <paramref name="key"/>.</summary>
    public bool Contains(string key) => scopes.Contains(key);
}
