using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ApiGateway;

/// <summary>
/// Builds and runs the gateway as a standalone service: configuration, OpenTelemetry, health probes,
/// OpenAPI and the proxy routes. This is the whole container — behaviour comes from configuration.
/// </summary>
public static class GatewayHost
{
    /// <summary>Service name reported to OpenTelemetry when <c>OTEL_SERVICE_NAME</c> is unset.</summary>
    public const string DefaultServiceName = "api-gateway";

    /// <summary>
    /// Where the mounted route table is read from, overridable with <c>GATEWAY_ROUTE_FILE</c>.
    /// Optional: with nothing mounted the gateway starts with the empty table from appsettings.json.
    /// </summary>
    public static string RouteFilePath =>
        Environment.GetEnvironmentVariable("GATEWAY_ROUTE_FILE") is { Length: > 0 } path
            ? path
            : "/config/routes.json";

    /// <summary>Builds the gateway <see cref="WebApplication"/> and runs it until shutdown.</summary>
    public static Task RunAsync(string[] args) => Build(args).RunAsync();

    /// <summary>
    /// Builds the gateway application without running it, for tests and for hosts that need to
    /// add their own services or middleware first.
    /// </summary>
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);

        // Configuration is layered, lowest precedence first:
        //
        //   1. appsettings.json   — baked into the image: logging, gateway defaults, empty route table
        //   2. RouteFilePath      — mounted route table (ConfigMap / bind mount), carrying proxyConfig
        //   3. DOTNET_* env vars  — per-environment overrides, chiefly EndPoints
        //
        // The route table is a *separate* file rather than a replacement appsettings.json so that
        // mounting it cannot silently drop the image's own defaults. It is watched: edit the mounted
        // file and RouteTableProvider rebuilds without a restart.
        builder.Configuration.AddJsonFile(source =>
        {
            source.Path = RouteFilePath;
            source.Optional = true;
            source.ReloadOnChange = true;
            // Required with this overload: it roots a file provider at the path's directory.
            // Without it an absolute path is resolved against the content root and never found,
            // leaving the gateway silently running with an empty route table.
            source.ResolveFileProvider();

            // Without this handler a malformed route file is swallowed entirely: the configuration
            // provider keeps the last good values and reports nothing, so an operator who pushes
            // broken JSON sees their change simply not take effect, with no diagnostic anywhere.
            // Keep ignoring it — serving the last good table beats failing — but say so loudly.
            source.OnLoadException = ctx =>
            {
                ctx.Ignore = true;
                Console.Error.WriteLine(
                    $"[api-gateway] Route file '{RouteFilePath}' could not be parsed and was ignored; "
                    + $"continuing with the previous route table. {ctx.Exception?.Message}");
            };
        });

        builder.Configuration.AddEnvironmentVariables("DOTNET_");

        builder.Services.Configure<JsonOptions>(o =>
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault);

        AddTelemetry(builder);

        builder.Services.AddHealthChecks();
        builder.Services.AddApiGateway(builder.Configuration);

        var app = builder.Build();

        // Liveness and readiness are mapped before the catch-all so the proxy never swallows them.
        app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
        app.MapHealthChecks("/health/ready");
        // Bare /health is the platform-wide convention (test harnesses, compose healthchecks, older probes all poll
        // it), and without it the catch-all proxy answers 404. Same meaning as readiness.
        app.MapHealthChecks("/health");

        app.MapOpenApi("/swagger/{documentName}/swagger.json");

        app.MapApiGateway();

        // Build the route table now rather than on the first request, so invalid configuration is a
        // startup failure and the loaded route count is visible in the logs before traffic arrives.
        app.Services.GetRequiredService<RouteTableProvider>();

        return app;
    }

    /// <summary>
    /// Wires traces, metrics and logs to an OTLP endpoint. Everything is driven by the standard
    /// <c>OTEL_*</c> environment variables (<c>OTEL_EXPORTER_OTLP_ENDPOINT</c>, <c>OTEL_SERVICE_NAME</c>,
    /// <c>OTEL_RESOURCE_ATTRIBUTES</c>). With no endpoint configured the exporters stay inert, so the
    /// container runs fine without a collector.
    /// </summary>
    private static void AddTelemetry(WebApplicationBuilder builder)
    {
        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? DefaultServiceName;
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var enabled = !string.IsNullOrWhiteSpace(otlpEndpoint);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName).AddEnvironmentVariableDetector())
            .WithTracing(t =>
            {
                t.AddAspNetCoreInstrumentation(o => o.Filter = ctx => !IsProbe(ctx.Request.Path));
                t.AddHttpClientInstrumentation();
                if (enabled)
                    t.AddOtlpExporter();
            })
            .WithMetrics(m =>
            {
                m.AddAspNetCoreInstrumentation();
                m.AddHttpClientInstrumentation();
                if (enabled)
                    m.AddOtlpExporter();
            });

        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeScopes = true;
            o.IncludeFormattedMessage = true;
            if (enabled)
                o.AddOtlpExporter();
        });
    }

    /// <summary>Health probes are high-volume and carry no diagnostic value; keep them out of traces.</summary>
    private static bool IsProbe(PathString path) =>
        path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);
}
