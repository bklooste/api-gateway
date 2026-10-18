using System.Net.Sockets;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ApiGateway.Tests;

/// <summary>
/// The endpoint directory follows <c>EndPoints</c> after startup. Before it did, a route reload that renamed a
/// backend sent every request on that route to a name the gateway had never loaded, failing with
/// "BaseAddress must be set" until the pod was restarted.
/// </summary>
public class EndpointDirectoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IConfigurationRoot Config(params (string Name, string Url)[] endpoints) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(endpoints.ToDictionary(e => "EndPoints:" + e.Name, e => (string?)e.Url))
            .Build();

    private static EndpointDirectory NewDirectory(IConfiguration config, IEndpointResolver? resolver = null, int timeoutSeconds = 2) =>
        new(config, Options.Create(new GatewayOptions { EndpointResolveTimeoutSeconds = timeoutSeconds }),
            resolver ?? new Resolver((_, _) => Task.CompletedTask), NullLoggerFactory.Instance);

    [Fact]
    public async Task A_renamed_backend_is_routable_after_a_refresh_without_a_restart()
    {
        var config = Config(("reward_loyalty", "http://reward-loyalty:8080/"));
        var directory = NewDirectory(config);
        var proxy = new Proxy(directory, new NoBaseAddressClients());

        config["EndPoints:reward_loyalty"] = null;
        config["EndPoints:marketing_loyalty"] = "http://marketing-loyalty:8080/";
        await directory.RefreshAsync(Ct);

        Assert.Equal(new Uri("http://marketing-loyalty:8080/"), proxy.GetHttpClient("marketing_loyalty").BaseAddress);
        Assert.Null(proxy.GetEndpointUrl("reward_loyalty"));
    }

    [Fact]
    public async Task A_new_name_goes_live_even_when_its_host_does_not_resolve()
    {
        var config = Config(("a", "http://a/"));
        var directory = NewDirectory(config, Unresolvable("b"));

        config["EndPoints:b"] = "http://b/";
        await directory.RefreshAsync(Ct);

        Assert.Equal("http://b/", directory.GetUrl("b"));
    }

    [Fact]
    public async Task A_changed_url_that_does_not_resolve_keeps_the_last_known_good_until_it_does()
    {
        var config = Config(("loyalty", "http://old-loyalty/"));
        var resolvable = false;
        var directory = NewDirectory(config, new Resolver((uri, _) => resolvable
            ? Task.CompletedTask
            : throw new SocketException((int)SocketError.HostNotFound)));

        config["EndPoints:loyalty"] = "http://new-loyalty/";
        await directory.RefreshAsync(Ct);
        Assert.Equal("http://old-loyalty/", directory.GetUrl("loyalty"));

        resolvable = true;
        await directory.RefreshAsync(Ct);
        Assert.Equal("http://new-loyalty/", directory.GetUrl("loyalty"));
    }

    [Fact]
    public async Task A_slow_lookup_blocks_neither_readers_nor_the_other_endpoints()
    {
        var config = Config(("a_slow", "http://slow-old/"), ("b_fast", "http://fast-old/"));
        var slowStarted = new TaskCompletionSource();
        var directory = NewDirectory(config, new Resolver(async (uri, ct) =>
        {
            if (uri.Host != "slow-new")
                return;
            slowStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }), timeoutSeconds: 1);

        config["EndPoints:a_slow"] = "http://slow-new/";
        config["EndPoints:b_fast"] = "http://fast-new/";
        config["EndPoints:c_added"] = "http://added/";
        var refresh = directory.RefreshAsync(Ct);

        // Mid-pass: readers get an answer at once, from a consistent snapshot.
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.False(refresh.IsCompleted);
        Assert.Equal("http://slow-old/", directory.GetUrl("a_slow"));
        Assert.Equal("http://added/", directory.GetUrl("c_added"));   // new names are not held behind lookups

        await refresh.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("http://slow-old/", directory.GetUrl("a_slow"));  // timed out: last-known-good kept
        Assert.Equal("http://fast-new/", directory.GetUrl("b_fast"));  // processed after the slow one regardless
    }

    [Fact]
    public async Task An_endpoints_section_gone_empty_keeps_the_previous_endpoints()
    {
        var config = Config(("a", "http://a/"), ("b", "http://b/"));
        var directory = NewDirectory(config);

        config["EndPoints:a"] = null;
        config["EndPoints:b"] = null;
        await directory.RefreshAsync(Ct);

        Assert.Equal(2, directory.Current.Count);
    }

    [Fact]
    public async Task A_name_removed_from_configuration_is_removed()
    {
        var config = Config(("a", "http://a/"), ("b", "http://b/"));
        var directory = NewDirectory(config);

        config["EndPoints:b"] = null;
        await directory.RefreshAsync(Ct);

        Assert.Equal(["a"], directory.Current.Keys);
    }

    [Fact]
    public async Task An_invalid_url_never_replaces_a_good_one()
    {
        var config = Config(("a", "http://a/"));
        var directory = NewDirectory(config);

        config["EndPoints:a"] = "not a url";
        config["EndPoints:b"] = "also not a url";
        await directory.RefreshAsync(Ct);

        Assert.Equal("http://a/", directory.GetUrl("a"));
        Assert.Null(directory.GetUrl("b"));
    }

    [Fact]
    public async Task The_runner_refreshes_as_soon_as_configuration_reports_a_change()
    {
        var config = Config(("reward_loyalty", "http://reward-loyalty:8080/"));
        var options = Options.Create(new GatewayOptions());
        var directory = NewDirectory(config);
        using var runner = new EndpointRefresher(directory, config, options, NullLoggerFactory.Instance);
        await runner.StartAsync(Ct);

        config["EndPoints:marketing_loyalty"] = "http://marketing-loyalty:8080/";
        config.Reload();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (directory.GetUrl("marketing_loyalty") is null && DateTime.UtcNow < deadline)
            await Task.Delay(20, Ct);
        await runner.StopAsync(Ct);

        Assert.Equal("http://marketing-loyalty:8080/", directory.GetUrl("marketing_loyalty"));
    }

    private static Resolver Unresolvable(string host) => new((uri, _) => uri.Host == host
        ? throw new SocketException((int)SocketError.HostNotFound)
        : Task.CompletedTask);

    private sealed class Resolver(Func<Uri, CancellationToken, Task> resolve) : IEndpointResolver
    {
        public Task ResolveAsync(Uri endpoint, CancellationToken cancellationToken) => resolve(endpoint, cancellationToken);
    }

    /// <summary>Like the real factory for an unconfigured name: the BaseAddress comes from the directory or not at all.</summary>
    private sealed class NoBaseAddressClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
