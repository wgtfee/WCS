using ModelContextProtocol.Server;

namespace Wcs.Host.Mcp;

public sealed class WcsMcpOptions
{
    public bool Enabled { get; set; }
    public string Route { get; set; } = "/mcp";
}

public static class WcsMcpHosting
{
    public static WebApplicationBuilder AddWcsMcp(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("Mcp").Get<WcsMcpOptions>()
            ?? new WcsMcpOptions();
        Validate(options);
        builder.Services.AddSingleton(options);

        if (!options.Enabled)
            return builder;

        builder.Services
            .AddMcpServer()
            .WithHttpTransport(transport => transport.Stateless = true)
            .WithTools<WcsReadOnlyMcpTools>();

        return builder;
    }

    public static WebApplication MapWcsMcp(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<WcsMcpOptions>();
        if (options.Enabled)
            app.MapMcp(options.Route);
        return app;
    }

    private static void Validate(WcsMcpOptions options)
    {
        if (!options.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(options.Route) || !options.Route.StartsWith('/'))
            throw new InvalidOperationException("Mcp:Route must start with '/'.");
    }
}
