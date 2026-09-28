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

    /// <summary>
    /// True when no scopes are required or the caller satisfies every requirement.
    ///
    /// A requirement may offer alternatives separated by <c>|</c> — <c>"customers-r|customers-w"</c> is
    /// satisfied by either. The two predicate lists otherwise express exactly one OR group
    /// (<see cref="Any"/>) and one AND, so a route needing
    /// <c>(customers-r or customers-w) and (brand-r:{query:brand} or brand-r:*)</c> has one group too many:
    /// the brand group goes in <see cref="Any"/> and the other comes here as alternatives.
    /// <c>|</c> in an <see cref="Any"/> entry is deliberately not split — list the alternatives instead.
    /// </summary>
    public bool All(params string[] reqScopes) => reqScopes.Length == 0 || reqScopes.All(Satisfies);

    /// <summary>True when the caller holds <paramref name="requirement"/>, or any of its <c>|</c> alternatives.</summary>
    private bool Satisfies(string requirement) =>
        requirement.Contains('|', StringComparison.Ordinal)
            ? requirement.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(Contains)
            : Contains(requirement);

    /// <summary>True when the caller holds <paramref name="key"/>.</summary>
    public bool Contains(string key) => scopes.Contains(key);
}
