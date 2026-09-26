namespace ApiGateway.Tests;

/// <summary>Path matching and URL rewriting — the pure routing core, no host required.</summary>
public class RoutingTests
{
    [Theory]
    [InlineData("/v1/me/wallet", "v1/me/wallet", true)]
    [InlineData("/v1/me/wallet/withdraw", "v1/me/wallet", true)]   // template matches a prefix
    [InlineData("/v1/me", "v1/me/wallet", false)]                  // path shorter than template
    [InlineData("/V1/ME/WALLET", "v1/me/wallet", true)]            // case-insensitive
    [InlineData("/v1/bet/abc/cancel", "v1/bet/{betId}/cancel", true)]
    [InlineData("/v1/bet/abc/settle", "v1/bet/{betId}/cancel", false)]
    public void PathMatchesTemplate_matches_by_segment(string path, string template, bool expected) =>
        Assert.Equal(expected, HttpMessageLogic.PathMatchesTemplate(path, template));

    [Fact]
    public void RewriteUrl_substitutes_path_parameters()
    {
        var result = HttpMessageLogic.RewriteUrl("v1/admin/bet/abc/cancel?q=1", "v1/admin/bet/{betId}/cancel", "v1/bet/{betId}/cancel");
        Assert.Equal("v1/bet/abc/cancel?q=1", result);
    }

    [Fact]
    public void RewriteUrl_preserves_trailing_segments_beyond_the_template()
    {
        var result = HttpMessageLogic.RewriteUrl("v2/event/sports/soccer/epl", "v2/event/sports", "v1/sport");
        Assert.Equal("v1/sport/soccer/epl", result);
    }

    [Fact]
    public void RewriteUrl_substitutes_userId_from_the_identity_header()
    {
        var result = HttpMessageLogic.RewriteUrl("v1/me/settings", "v1/me/settings", "v1/user/{userId}/settings", "user 42");
        Assert.Equal("v1/user/user%2042/settings", result);
    }

    [Fact]
    public void RewriteUrl_keeps_the_query_string_untouched()
    {
        var result = HttpMessageLogic.RewriteUrl("v3/nav?brand=x&cats=a&cats=b", "v3/nav", "v3/event/nav");
        Assert.Equal("v3/event/nav?brand=x&cats=a&cats=b", result);
    }
}
