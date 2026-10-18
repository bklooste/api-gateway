using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiGateway;

/// <summary>
/// The live map of downstream service name to base URL, built from the <c>EndPoints</c> section and kept
/// current by <see cref="EndpointRefresher"/>.
///
/// <c>EndPoints</c> usually lives in the same hot-reloaded route file as <c>proxyConfig</c>. Before this
/// existed the map was read once at startup, so a route reload that introduced a new <c>ProxyName</c> (a
/// renamed backend, say) routed to a name the gateway did not know, and every request on it failed with
/// "BaseAddress must be set" until the pod was restarted.
///
/// The map is an immutable snapshot behind a volatile reference, exactly like the route table in
/// <see cref="RouteTableProvider"/>: a request does one reference read and never waits on a refresh, and a
/// refresh builds each change into a new snapshot before publishing it, so no reader sees a half-applied pass.
/// </summary>
public sealed class EndpointDirectory
{
    private const string SectionName = "EndPoints";

    private readonly IConfiguration _configuration;
    private readonly IEndpointResolver _resolver;
    private readonly GatewayOptions _options;
    private readonly ILogger _logger;

    // Serialises refresh passes; the single writer is what lets each publish be a plain volatile write.
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private volatile ImmutableDictionary<string, string> _current;

    public EndpointDirectory(IConfiguration configuration, IOptions<GatewayOptions> options, IEndpointResolver resolver, ILoggerFactory loggerFactory)
    {
        _configuration = configuration;
        _resolver = resolver;
        _options = options.Value;
        _logger = loggerFactory.CreateLogger<EndpointDirectory>();

        // Startup takes the configured values as they stand, without resolving: the gateway must not wait on
        // DNS to start, and there is no last-known-good value yet that a failed lookup could protect.
        _current = ImmutableDictionary.CreateRange(StringComparer.Ordinal, ReadConfigured());
        _logger.LogInformation("Endpoint directory loaded with {EndpointCount} endpoints", _current.Count);
    }

    /// <summary>The active endpoints, name to base URL. An immutable snapshot: safe to enumerate while a refresh runs.</summary>
    public IReadOnlyDictionary<string, string> Current => _current;

    /// <summary>The base URL for <paramref name="name"/>, or null when it is not in the directory.</summary>
    public string? GetUrl(string name) => _current.TryGetValue(name, out var url) ? url : null;

    /// <summary>
    /// Re-reads <c>EndPoints</c> and applies the differences to the live directory, publishing after each one.
    ///
    /// <list type="bullet">
    /// <item>A name new to the directory goes live at once, unresolved: it has no previous value to protect,
    /// and a renamed backend must not wait behind lookups for unrelated ones.</item>
    /// <item>A name whose URL changed is adopted only once the new host resolves, checked one entry at a time
    /// with <see cref="GatewayOptions.EndpointResolveTimeoutSeconds"/> each, so one slow lookup delays only
    /// itself. A failed lookup keeps that name's last-known-good URL and is retried on the next pass.</item>
    /// <item>A name gone from configuration is removed.</item>
    /// </list>
    ///
    /// A route file that does not parse, or an <c>EndPoints</c> section that has gone empty, changes nothing:
    /// either is far likelier to be a bad config push than an intent to stop routing anywhere.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            // Same guard as the route table: the file provider clears its data before reporting a parse
            // error, so trusting IConfiguration here would remove every endpoint on one bad push.
            if (!RouteTableProvider.RouteFileIsParseable(out var parseError))
            {
                _logger.LogError("Route file {Path} is not valid JSON ({Error}); keeping the previous {EndpointCount} endpoints",
                    GatewayHost.RouteFilePath, parseError, _current.Count);
                return;
            }

            var configured = ReadConfigured();
            if (configured.Count == 0 && !_current.IsEmpty)
            {
                _logger.LogWarning("{Section} is empty; keeping the previous {EndpointCount} endpoints", SectionName, _current.Count);
                return;
            }

            foreach (var (name, url) in configured)
            {
                if (_current.ContainsKey(name))
                    continue;
                _current = _current.SetItem(name, url);
                _logger.LogInformation("Endpoint {Name} added: {Url}", name, url);
            }

            foreach (var (name, url) in configured)
            {
                var live = _current[name];
                if (live == url)
                    continue;

                if (await HostResolvesAsync(name, url, cancellationToken))
                {
                    _current = _current.SetItem(name, url);
                    _logger.LogInformation("Endpoint {Name} moved: {Previous} -> {Url}", name, live, url);
                }
                else
                {
                    _logger.LogWarning("Endpoint {Name} is configured as {Url}, which does not resolve yet; keeping {Previous}",
                        name, url, live);
                }
            }

            var removed = _current.Keys.Where(name => !configured.ContainsKey(name)).ToList();
            if (removed.Count > 0)
            {
                _current = _current.RemoveRange(removed);
                _logger.LogInformation("Endpoints removed: {Names}", string.Join(", ", removed));
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<bool> HostResolvesAsync(string name, string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.EndpointResolveTimeoutSeconds));
        try
        {
            await _resolver.ResolveAsync(new Uri(url), timeout.Token);
            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Resolving endpoint {Name} ({Url}) failed", name, url);
            return false;
        }
    }

    /// <summary>The configured endpoints; a value that is not an absolute URL is logged and left out.</summary>
    private Dictionary<string, string> ReadConfigured()
    {
        var configured = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in _configuration.GetSection(SectionName).GetChildren())
        {
            if (entry.Value is not { } url)
                continue;
            if (Uri.TryCreate(url, UriKind.Absolute, out _))
                configured[entry.Key] = url;
            else
                _logger.LogError("Endpoint {Name} has an invalid URL {Url} and is ignored", entry.Key, url);
        }
        return configured;
    }
}

/// <summary>Checks that a changed endpoint's host can be reached by name before the gateway switches to it.</summary>
public interface IEndpointResolver
{
    /// <summary>Completes when <paramref name="endpoint"/>'s host resolves; throws when it does not.</summary>
    Task ResolveAsync(Uri endpoint, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves the host through DNS. Only a gate on adopting a changed URL: the addresses themselves are not
/// kept, since <see cref="IHttpClientFactory"/> rotates its handlers and so re-resolves on its own.
/// </summary>
public sealed class DnsEndpointResolver : IEndpointResolver
{
    public async Task ResolveAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        if (endpoint.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            return;

        var addresses = await Dns.GetHostAddressesAsync(endpoint.DnsSafeHost, cancellationToken);
        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);
    }
}
