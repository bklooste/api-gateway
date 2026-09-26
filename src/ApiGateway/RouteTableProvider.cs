using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace ApiGateway;

/// <summary>
/// Holds the active route table and rebuilds it when configuration changes.
///
/// Rebuilding never happens on a request thread and never blocks one: the current table keeps
/// serving until a replacement has been built in full, then the reference is swapped. Building is
/// therefore allowed to be as expensive as it likes — it happens at most once per config change,
/// whereas the table is read on every request.
///
/// A failed rebuild (bad config pushed to the mounted file) is logged and discarded; the last good
/// table stays in service rather than taking the gateway down.
/// </summary>
public sealed class RouteTableProvider : IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly IDisposable? _changeSubscription;
    private readonly Timer? _pollTimer;
    private int _rebuilding;

    private volatile ProxyConfig[] _current;

    /// <summary>The active route table. Reading this is a volatile read — no lock on the request path.</summary>
    public ProxyConfig[] Current => _current;

    public RouteTableProvider(IConfiguration configuration, IOptions<GatewayOptions> options, ILoggerFactory loggerFactory)
    {
        _configuration = configuration;
        _logger = loggerFactory.CreateLogger<RouteTableProvider>();

        // Built eagerly so invalid configuration fails at startup rather than on the first request.
        _current = HttpMessageLogic.LoadProxyConfig(configuration);
        _logger.LogInformation("Route table loaded with {RouteCount} routes", _current.Length);

        // Primary signal: the file providers backing IConfiguration watch their files, so an edit to
        // the mounted route file rebuilds within seconds.
        _changeSubscription = ChangeToken.OnChange(configuration.GetReloadToken, Rebuild);

        // Fallback: file watching does not work on every filesystem a container may mount. The poll
        // guarantees changes are eventually picked up even where the watcher is silent.
        var minutes = options.Value.ConfigReloadMinutes;
        if (minutes > 0)
        {
            var period = TimeSpan.FromMinutes(minutes);
            _pollTimer = new Timer(_ => Rebuild(), null, period, period);
        }
    }

    private void Rebuild()
    {
        // Watchers commonly fire more than once for a single write; collapse overlapping rebuilds.
        if (Interlocked.Exchange(ref _rebuilding, 1) == 1)
            return;

        try
        {
            // A malformed route file does NOT surface as an exception here. The file configuration
            // provider clears its own data *before* reporting the parse failure, so IConfiguration
            // simply goes empty and this would happily swap in a zero-route table — turning one bad
            // config push into a total outage. Validate the file itself before trusting a reload.
            if (!RouteFileIsParseable(out var parseError))
            {
                _logger.LogError("Route file {Path} is not valid JSON ({Error}); keeping the previous {RouteCount} routes",
                    GatewayHost.RouteFilePath, parseError, _current.Length);
                return;
            }

            var next = HttpMessageLogic.LoadProxyConfig(_configuration);
            var previous = _current;
            _current = next;                          // atomic swap; in-flight requests keep the old table

            if (previous.Length != next.Length)
                _logger.LogInformation("Route table reloaded: {Previous} -> {Current} routes", previous.Length, next.Length);
            else
                _logger.LogInformation("Route table reloaded with {RouteCount} routes", next.Length);
        }
        catch (Exception ex)
        {
            // Keep serving the last good table — a bad config push must not take the gateway down.
            _logger.LogError(ex, "Route table reload failed; keeping the previous {RouteCount} routes", _current.Length);
        }
        finally
        {
            Interlocked.Exchange(ref _rebuilding, 0);
        }
    }

    /// <summary>
    /// True when no route file is mounted, or the mounted one is valid JSON. Guards against a
    /// half-written or corrupt file being promoted into service as an empty route table.
    /// </summary>
    private static bool RouteFileIsParseable(out string? error)
    {
        error = null;
        var path = GatewayHost.RouteFilePath;

        if (!File.Exists(path))
            return true;                      // nothing mounted: the baked-in defaults are authoritative

        try
        {
            // Must match what the JSON configuration provider accepts, or this guard would reject a
            // file the gateway would otherwise have loaded happily — comments especially, since the
            // shipped example route table is annotated.
            using var _ = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void Dispose()
    {
        _changeSubscription?.Dispose();
        _pollTimer?.Dispose();
    }
}
