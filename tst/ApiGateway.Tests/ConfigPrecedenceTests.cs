using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace ApiGateway.Tests;

/// <summary>
/// GLOBAL_ env vars are environment-wide defaults: lowest precedence, below appsettings.json; DOTNET_ beats both.
/// Each test uses its own key, since env vars are process-wide.
/// </summary>
public class ConfigPrecedenceTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("precedence-").FullName;
    private readonly List<string> vars = [];

    private void Set(string name, string value)
    {
        vars.Add(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    /// <summary>The source order GatewayHost builds: appsettings.json, GLOBAL_ inserted below it, then DOTNET_.</summary>
    private IConfigurationRoot Build(string key, string? appsettingsValue)
    {
        File.WriteAllText(Path.Combine(dir, "appsettings.json"),
            appsettingsValue is null ? "{}" : $$"""{ "Precedence": { "{{key}}": "{{appsettingsValue}}" } }""");
        return new ConfigurationBuilder()
            .AddJsonFile(new PhysicalFileProvider(dir), "appsettings.json", optional: true, reloadOnChange: false)
            .AddGlobalDefaults()
            .AddEnvironmentVariables("DOTNET_")
            .Build();
    }

    [Fact]
    public void A_GLOBAL_variable_alone_is_read()
    {
        Set("GLOBAL_Precedence__Alone", "global");
        Assert.Equal("global", Build("Alone", null)["Precedence:Alone"]);
    }

    [Fact]
    public void Appsettings_beats_GLOBAL()
    {
        Set("GLOBAL_Precedence__Json", "global");
        Assert.Equal("appsettings", Build("Json", "appsettings")["Precedence:Json"]);
    }

    [Fact]
    public void DOTNET_beats_appsettings_and_GLOBAL()
    {
        Set("GLOBAL_Precedence__Dotnet", "global");
        Set("DOTNET_Precedence__Dotnet", "dotnet");
        Assert.Equal("dotnet", Build("Dotnet", "appsettings")["Precedence:Dotnet"]);
    }

    [Fact]
    public void Without_an_appsettings_source_it_throws_rather_than_append_on_top()
    {
        Assert.Throws<InvalidOperationException>(() => new ConfigurationBuilder().AddGlobalDefaults());
    }

    [Fact]
    public void The_gateway_host_reads_GLOBAL_below_its_appsettings()
    {
        Set("GLOBAL_EndPoints__precedence_probe", "http://global/");
        Set("GLOBAL_gateway__headers__brand", "from-global"); // appsettings.json sets "brand"
        using var app = GatewayHost.Build([]);
        var config = (IConfiguration)app.Services.GetService(typeof(IConfiguration))!;

        Assert.Equal("http://global/", config["EndPoints:precedence_probe"]);
        Assert.Equal("brand", config["gateway:headers:brand"]);
    }

    public void Dispose()
    {
        foreach (var name in vars) Environment.SetEnvironmentVariable(name, null);
        Directory.Delete(dir, recursive: true);
    }
}
