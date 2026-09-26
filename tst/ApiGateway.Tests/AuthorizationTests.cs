namespace ApiGateway.Tests;

/// <summary>Scope predicate evaluation.</summary>
public class AuthorizationTests
{
    [Fact]
    public void Any_passes_when_the_caller_holds_one_of_the_required_scopes()
    {
        var helper = new AuthorizationHelper(["events-r", "bet-r"]);

        Assert.True(helper.Any("events-w", "events-r"));
        Assert.False(helper.Any("events-w", "admin"));
    }

    [Fact]
    public void All_requires_every_scope()
    {
        var helper = new AuthorizationHelper(["events-r", "admin"]);

        Assert.True(helper.All("events-r", "admin"));
        Assert.False(helper.All("events-r", "events-w"));
    }

    [Fact]
    public void An_empty_requirement_list_passes()
    {
        var helper = new AuthorizationHelper([]);

        Assert.True(helper.Any());
        Assert.True(helper.All());
    }

    [Fact]
    public void A_caller_with_no_scopes_fails_any_requirement()
    {
        var helper = new AuthorizationHelper(null);

        Assert.False(helper.Any("events-r"));
        Assert.False(helper.All("events-r"));
    }

    [Fact]
    public void Scope_matching_is_case_sensitive()
    {
        // Scopes are opaque identifiers issued by the authorization server. Case-folding them
        // would let "Admin" satisfy a requirement for "admin".
        Assert.False(new AuthorizationHelper(["Admin"]).Contains("admin"));
    }

    [Fact]
    public void There_is_no_ambient_bypass()
    {
        // Guards against reintroducing a "skip the check when <condition>" escape hatch.
        // Under a debugger this test would previously have passed for the wrong reason.
        Assert.False(new AuthorizationHelper([]).Contains("admin"));
        Assert.False(new AuthorizationHelper(["other"]).All("admin"));
        Assert.False(new AuthorizationHelper(["other"]).Any("admin"));
    }
}
