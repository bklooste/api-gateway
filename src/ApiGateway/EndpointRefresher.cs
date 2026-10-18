using System.Threading.Channels;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace ApiGateway;

/// <summary>
/// Background runner that keeps <see cref="EndpointDirectory"/> current. A pass runs whenever configuration
/// reports a change (the route file was edited) and every <see cref="GatewayOptions.EndpointRefreshMinutes"/>
/// as a backstop for a missed change notification and a retry for changed URLs that did not resolve yet.
///
/// Passes never overlap and never queue up: a trigger arriving mid-pass schedules exactly one more pass.
/// Requests are never involved — they read the directory's current snapshot.
/// </summary>
public sealed class EndpointRefresher(EndpointDirectory directory, IConfiguration configuration, IOptions<GatewayOptions> options, ILoggerFactory loggerFactory)
    : BackgroundService
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<EndpointRefresher>();

    // Capacity one with extra writes dropped: this is the "run again when done" flag, not a work queue.
    private readonly Channel<bool> _trigger = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true
    });

    private IDisposable? _onChange;
    private Timer? _timer;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Subscribed here rather than in ExecuteAsync, which the host may start later on another thread:
        // a change reported in between would otherwise wait for the timer.
        _onChange = ChangeToken.OnChange(configuration.GetReloadToken, RequestRefresh);

        var minutes = options.Value.EndpointRefreshMinutes;
        if (minutes > 0)
        {
            var period = TimeSpan.FromMinutes(minutes);
            _timer = new Timer(_ => RequestRefresh(), null, period, period);
        }

        return base.StartAsync(cancellationToken);
    }

    private void RequestRefresh() => _trigger.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var _ in _trigger.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await directory.RefreshAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // The directory keeps its last-known-good snapshot; the next trigger tries again.
                    _logger.LogError(ex, "Endpoint directory refresh failed; keeping the previous endpoints");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override void Dispose()
    {
        _onChange?.Dispose();
        _timer?.Dispose();
        base.Dispose();
    }
}
