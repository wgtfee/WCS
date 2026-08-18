using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Wcs.Host.Mcp;

namespace Wcs.Core.Tests.Mcp;

public sealed class WcsMcpAuthenticationTests
{
    [Fact]
    public void EnabledMcp_RequiresAuthority()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mcp:Enabled"] = "true",
            ["Mcp:Route"] = "/mcp",
            ["Mcp:Authentication:Audience"] = "industrial-platform"
        });

        Assert.Throws<InvalidOperationException>(() => WcsMcpHosting.AddWcsMcp(builder));
    }

    [Fact]
    public void EnabledMcp_RequiresAudience()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mcp:Enabled"] = "true",
            ["Mcp:Route"] = "/mcp",
            ["Mcp:Authentication:Authority"] = "https://identity.example.invalid"
        });

        Assert.Throws<InvalidOperationException>(() => WcsMcpHosting.AddWcsMcp(builder));
    }

    [Fact]
    public void DisabledMcp_DoesNotRequireAuthenticationSettings()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mcp:Enabled"] = "false"
        });

        var result = WcsMcpHosting.AddWcsMcp(builder);

        Assert.Same(builder, result);
    }
}
