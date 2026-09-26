namespace ApiGateway.Tests;

/// <summary>
/// The URL handed to the cache service. The upstream URL is carried as an encoded query parameter,
/// so encoding mistakes here silently route the cache at the wrong origin.
/// </summary>
public class CacheUrlTests
{
    [Fact]
    public void BuildCacheUrl_encodes_the_upstream_url()
    {
        var url = HttpMessageLogic.BuildCacheUrl("http://offer-offerings:8080/", "v3/event/nav?brand=x", new CacheConfig());

        Assert.Equal("cache?url=http%3A%2F%2Foffer-offerings%3A8080%2Fv3%2Fevent%2Fnav%3Fbrand%3Dx", url);
    }

    [Fact]
    public void BuildCacheUrl_emits_key_vars_headers_and_ttl()
    {
        var cache = new CacheConfig
        {
            CacheVars = ["brand", "categories"],
            CacheHeaders = ["userid"],
            TtlSeconds = 30
        };

        var url = HttpMessageLogic.BuildCacheUrl("http://svc:8080", "v1/nav", cache);

        Assert.StartsWith("cache?cacheVar=brand&cacheVar=categories&cacheHeader=userid&url=", url);
        Assert.EndsWith("&ttl=30", url);
    }

    [Fact]
    public void BuildCacheUrl_does_not_double_the_separating_slash()
    {
        var withSlash = HttpMessageLogic.BuildCacheUrl("http://svc:8080/", "/v1/nav", new CacheConfig());
        var withoutSlash = HttpMessageLogic.BuildCacheUrl("http://svc:8080", "v1/nav", new CacheConfig());

        Assert.Equal(withoutSlash, withSlash);
        Assert.Contains(Uri.EscapeDataString("http://svc:8080/v1/nav"), withSlash);
    }
}
