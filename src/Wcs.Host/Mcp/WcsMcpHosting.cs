using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace Wcs.Host.Mcp;

public sealed class WcsMcpOptions
{
    public bool Enabled { get; set; }
    public string Route { get; set; } = "/mcp";
    public WcsMcpAuthenticationOptions Authentication { get; set; } = new();
}

public sealed class WcsMcpAuthenticationOptions
{
    public string Authority { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public bool RequireHttpsMetadata { get; set; } = true;
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
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.Authority = options.Authentication.Authority;
                jwt.Audience = options.Authentication.Audience;
                jwt.RequireHttpsMetadata = options.Authentication.RequireHttpsMetadata;
            });
        builder.Services.AddAuthorization();

        builder.Services
            .AddMcpServer()
            .WithHttpTransport(transport => transport.Stateless = true)
            .WithTools<WcsReadOnlyMcpTools>();

        return builder;
    }

    public static WebApplication MapWcsMcp(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<WcsMcpOptions>();
        if (!options.Enabled)
            return app;

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMcp(options.Route).RequireAuthorization(new AuthorizeAttribute());
        return app;
    }

    private static void Validate(WcsMcpOptions options)
    {
        if (!options.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(options.Route) || !options.Route.StartsWith('/'))
            throw new InvalidOperationException("Mcp:Route must start with '/'.");
        if (string.IsNullOrWhiteSpace(options.Authentication.Authority))
            throw new InvalidOperationException("Mcp:Authentication:Authority is required when MCP is enabled.");
        if (string.IsNullOrWhiteSpace(options.Authentication.Audience))
            throw new InvalidOperationException("Mcp:Authentication:Audience is required when MCP is enabled.");
    }
}
