using Microsoft.Extensions.Configuration;

namespace ApiGateway.Tests;

/// <summary>
/// The URL handed to the translate service, alone and behind the cache. The upstream path and query stay at
/// the top level so the cache in front still keys on them.
/// </summary>
public class TranslateUrlTests
{
    [Fact]
    public void BuildTranslateUrl_keeps_the_upstream_query_at_the_top_level()
    {
        var url = HttpMessageLogic.BuildTranslateUrl("http://offer-offering:8080/", "/v3/nav?brand=x&lang=zh");

        Assert.Equal("translate/v3/nav?brand=x&lang=zh&translateUpstream=http%3A%2F%2Foffer-offering%3A8080", url);
    }

    [Fact]
    public void BuildTranslateUrl_starts_a_query_when_there_is_none()
    {
        var url = HttpMessageLogic.BuildTranslateUrl("http://svc:8080", "v1/nav");

        Assert.Equal("translate/v1/nav?translateUpstream=http%3A%2F%2Fsvc%3A8080", url);
    }

    [Fact]
    public void Cache_in_front_of_translate_targets_the_translate_service()
    {
        var translateUrl = HttpMessageLogic.BuildTranslateUrl("http://svc:8080", "v1/nav?brand=x&lang=zh");

        var url = HttpMessageLogic.BuildCacheUrl("http://util-translate:8080/", translateUrl, new CacheConfig { CacheVars = ["brand", "lang"] });

        Assert.Equal(
            "cache?cacheVar=brand&cacheVar=lang&url=" + Uri.EscapeDataString(
                "http://util-translate:8080/translate/v1/nav?brand=x&lang=zh&translateUpstream=http%3A%2F%2Fsvc%3A8080"),
            url);
    }

    private static ProxyConfig Load(Dictionary<string, string?> values) =>
        Assert.Single(HttpMessageLogic.LoadProxyConfig(new ConfigurationBuilder().AddInMemoryCollection(values).Build()));

    [Fact]
    public void Translate_is_off_unless_the_route_sets_it()
    {
        var route = Load(new() { ["proxyConfig:0:ProxyName"] = "a", ["proxyConfig:0:ApiUrl"] = "v1/a" });

        Assert.False(route.Translate);
    }

    [Fact]
    public void A_translated_cached_route_keys_on_lang()
    {
        var route = Load(new()
        {
            ["proxyConfig:0:ProxyName"] = "a",
            ["proxyConfig:0:ApiUrl"] = "v1/a",
            ["proxyConfig:0:Translate"] = "true",
            ["proxyConfig:0:Cache:CacheVars:0"] = "brand"
        });

        Assert.True(route.Translate);
        Assert.Equal(["brand", "lang"], route.Cache!.CacheVars!);
    }

    [Fact]
    public void An_untranslated_cached_route_keeps_its_cache_key()
    {
        var route = Load(new()
        {
            ["proxyConfig:0:ProxyName"] = "a",
            ["proxyConfig:0:ApiUrl"] = "v1/a",
            ["proxyConfig:0:Cache:CacheVars:0"] = "brand"
        });

        Assert.Equal(["brand"], route.Cache!.CacheVars!);
    }
}
