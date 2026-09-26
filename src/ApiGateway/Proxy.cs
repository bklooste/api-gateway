using Microsoft.Extensions.Configuration;
using Microsoft.OpenApi;

namespace ApiGateway;

/// <summary>Resolves downstream <see cref="HttpClient"/>s by name from the <c>EndPoints</c> config section.</summary>
public class Proxy
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Dictionary<string, string> _endPointUrls = [];

    public Proxy(IConfiguration config, IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        foreach (var entry in config.GetSection("EndPoints").GetChildren())
        {
            if (entry.Value != null)
                _endPointUrls.Add(entry.Key, entry.Value);
        }
    }

    /// <summary>All configured endpoints, name to base URL.</summary>
    public IReadOnlyDictionary<string, string> EndPointUrls => _endPointUrls;

    /// <summary>Named client; its BaseAddress defaults to <c>EndPoints:{name}</c> when not set by the factory.</summary>
    public HttpClient GetHttpClient(string name)
    {
        var client = _httpClientFactory.CreateClient(name);
        if (client.BaseAddress == null && _endPointUrls.TryGetValue(name, out var url))
            client.BaseAddress = new Uri(url);
        return client;
    }

    /// <summary>The configured base URL for <paramref name="name"/>, or null when unregistered.</summary>
    public string? GetEndpointUrl(string name) => _endPointUrls.TryGetValue(name, out var url) ? url : null;

    /// <summary>The merged OpenAPI document, set by <see cref="OpenApiMerger"/>.</summary>
    public OpenApiDocument? ApiDocument { get; set; }
}
