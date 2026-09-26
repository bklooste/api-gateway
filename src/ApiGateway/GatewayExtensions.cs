using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
        services.Configure<GatewayOptions>(configuration.GetSection(GatewayOptions.SectionName));
        services.AddOpenApi("v1", o =>
        {
            o.OpenApiVersion = OpenApiSpecVersion.OpenApi3_0;
            o.AddDocumentTransformer<OpenApiMerger>();
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
