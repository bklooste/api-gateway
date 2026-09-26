using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApiGateway;

/// <summary>Serializer options for responses the gateway reshapes (the <c>ResponsePath</c> projection).</summary>
public static class GatewayJson
{
    /// <summary>Web defaults, string enums, and default-valued properties omitted.</summary>
    public static readonly JsonSerializerOptions IgnoreDefault = new(JsonSerializerDefaults.Web)
    {
        IgnoreReadOnlyProperties = true,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "JsonStringEnumConverter is used intentionally for non-AOT contexts")]
    static GatewayJson() => IgnoreDefault.Converters.Add(new JsonStringEnumConverter());
}
