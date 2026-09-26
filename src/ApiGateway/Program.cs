using ApiGateway;

// Container HEALTHCHECK: the chiselled runtime image has no shell or curl, so the app probes itself.
if (args.Contains("--healthcheck"))
{
    var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';')[0] ?? "8080";
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try { return (await http.GetAsync($"http://localhost:{port}/health/ready")).IsSuccessStatusCode ? 0 : 1; }
    catch { return 1; }
}

// The gateway is infrastructure: routing, authorization and URL rewriting are all driven by the
// proxyConfig/EndPoints/gateway sections in configuration. There is no service-specific code here
// by design — a new route is a config change, not a deployment of new code.
await GatewayHost.RunAsync(args);
return 0;
