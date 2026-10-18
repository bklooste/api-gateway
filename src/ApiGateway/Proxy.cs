using Microsoft.OpenApi;

namespace ApiGateway;

/// <summary>Resolves downstream <see cref="HttpClient"/>s by name from the live <see cref="EndpointDirectory"/>.</summary>
public class Proxy(EndpointDirectory endpoints, IHttpClientFactory httpClientFactory)
{
    /// <summary>All current endpoints, name to base URL.</summary>
    public IReadOnlyDictionary<string, string> EndPointUrls => endpoints.Current;

    /// <summary>Named client; its BaseAddress defaults to the endpoint's current URL when not set by the factory.</summary>
    public HttpClient GetHttpClient(string name)
    {
        var client = httpClientFactory.CreateClient(name);
        if (client.BaseAddress == null && endpoints.GetUrl(name) is { } url)
            client.BaseAddress = new Uri(url);
        return client;
    }

    /// <summary>The current base URL for <paramref name="name"/>, or null when unregistered.</summary>
    public string? GetEndpointUrl(string name) => endpoints.GetUrl(name);

    /// <summary>The merged OpenAPI document, set by <see cref="OpenApiMerger"/>.</summary>
    public OpenApiDocument? ApiDocument { get; set; }
}
