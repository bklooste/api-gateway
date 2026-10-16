using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace ApiGateway;

/// <summary>Registration and endpoint mapping for a config-driven API gateway.</summary>
public static class GatewayExtensions
{
    /// <summary>
    /// Registers <see cref="Proxy"/>, <see cref="HttpMessageLogic"/> and the "v1" OpenAPI document with
    /// <see cref="OpenApiMerger"/>. Routes come from <c>proxyConfig</c>, endpoints from <c>EndPoints</c>,
    /// optional <c>globalRequiredScopes</c> are required on every route, and <c>gateway</c> holds
    /// <see cref="GatewayOptions"/>.
    /// </summary>
    public static IServiceCollection AddApiGateway(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GatewayOptions>()
            .Bind(configuration.GetSection(GatewayOptions.SectionName))
            .Validate(o => GatewayOpenApiOptions.TryParseVersion(o.OpenApi.OutputVersion, out _),
                $"{GatewayOptions.SectionName}:openApi:outputVersion must be one of 3.0, 3.1 or 3.2.")
            .ValidateOnStart();
        services.AddOpenApi("v1", o => o.AddDocumentTransformer<OpenApiMerger>());
        // The merged document is emitted in the configured version (default 3.1). Validation above has
        // already rejected an unknown value by the time this runs.
        services.AddOptions<OpenApiOptions>("v1").Configure<IOptions<GatewayOptions>>((o, gateway) =>
        {
            if (GatewayOpenApiOptions.TryParseVersion(gateway.Value.OpenApi.OutputVersion, out var version))
                o.OpenApiVersion = version;
        });
        services.AddHttpContextAccessor();
        services.AddHttpClient();
        services.AddSingleton<Proxy>();
        services.AddSingleton<RouteTableProvider>();
        services.AddSingleton<HttpMessageLogic>();
        return services;
    }

    /// <summary>
    /// Maps the catch-all <c>/{**slug}</c> for GET/PUT/POST/PATCH/DELETE onto
    /// <see cref="HttpMessageLogic.ProcessCall"/>, plus <c>GET debug</c> when
    /// <see cref="GatewayOptions.EnableDebugEndpoint"/> is set. Add new routes to config, not code.
    /// </summary>
    public static IEndpointRouteBuilder MapApiGateway(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<IOptions<GatewayOptions>>().Value;

        if (options.EnableDebugEndpoint)
        {
            var headers = options.Headers;
            app.MapGet("debug", (HttpRequest request) =>
                    Results.Ok(new Dictionary<string, string?>
                    {
                        [headers.Scopes] = request.Headers[headers.Scopes],
                        [headers.Brand] = request.Headers[headers.Brand],
                        [headers.UserId] = request.Headers[headers.UserId]
                    }))
                .Produces<Dictionary<string, string>>()
                .ProducesValidationProblem()
                .WithTags("API-V1");
        }

        app.MapMethods("/{**slug}", [HttpMethods.Get, HttpMethods.Put, HttpMethods.Post, HttpMethods.Patch, HttpMethods.Delete],
                (HttpRequest request, [FromServices] HttpMessageLogic logic, [FromRoute] string slug) => logic.ProcessCall(request, slug))
            .Produces<Dictionary<string, string>>()
            .ProducesValidationProblem()
            .WithTags("API-V1");

        return app;
    }
}
