namespace Wcs.Simulator.Tests;

using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Wcs.Host.Controllers;
using Wcs.Simulator.HilVerification;

public sealed class SimulationHilSoftwareReadinessTests
{
    [Fact]
    public void DefaultOptions_AreDisabled()
    {
        Assert.False(new HilVerificationOptions().Enabled);
    }

    [Fact]
    public void Boundary_ProductionAlwaysFailsClosed()
    {
        var decision = HilEnvironmentBoundaryGuard.Evaluate("Production", Options());
        Assert.False(decision.Allowed);
        Assert.Contains("Production", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Boundary_OnlyAllowsConfiguredHilEnvironments()
    {
        Assert.True(HilEnvironmentBoundaryGuard.Evaluate("HIL", Options()).Allowed);
        Assert.True(HilEnvironmentBoundaryGuard.Evaluate("TrialRun", Options()).Allowed);
        Assert.False(HilEnvironmentBoundaryGuard.Evaluate("Development", Options()).Allowed);
        Assert.False(HilEnvironmentBoundaryGuard.Evaluate("", Options()).Allowed);
    }

    [Fact]
    public void Boundary_InvalidConfigurationFailsClosed()
    {
        var options = Options();
        options.AllowedEnvironments = ["HIL", "Production"];
        var decision = HilEnvironmentBoundaryGuard.Evaluate("HIL", options);
        Assert.False(decision.Allowed);
        Assert.Contains("Invalid", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Controller_ProductionReturns404EvenWhenConfigurationEnablesHil()
    {
        var controller = new HilVerificationController(new TestEnvironment("Production"), Configuration(enabled: true));
        Assert.IsType<NotFoundResult>(controller.GetStatus());
        Assert.IsType<NotFoundResult>(controller.GetAcceptanceRequirements());
    }

    [Fact]
    public void Controller_HilEnvironmentExposesReadOnlyStatusAndRequirements()
    {
        var controller = new HilVerificationController(new TestEnvironment("HIL"), Configuration(enabled: true));
        Assert.IsType<OkObjectResult>(controller.GetStatus());
        Assert.IsType<OkObjectResult>(controller.GetAcceptanceRequirements());
    }

    [Fact]
    public void Controller_DisabledConfigurationReturns404()
    {
        var controller = new HilVerificationController(new TestEnvironment("HIL"), Configuration(enabled: false));
        Assert.IsType<NotFoundResult>(controller.GetStatus());
    }

    [Fact]
    public void Controller_ExposesOnlyHttpGetActions()
    {
        var methods = typeof(HilVerificationController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.Equal(2, methods.Length);
        Assert.All(methods, method => Assert.NotEmpty(method.GetCustomAttributes<HttpGetAttribute>(inherit: true)));
        Assert.DoesNotContain(methods, method => method.GetCustomAttributes(inherit: true).Any(attribute =>
            attribute is HttpPostAttribute or HttpPutAttribute or HttpPatchAttribute or HttpDeleteAttribute));
    }

    private static HilVerificationOptions Options() => new()
    {
        Enabled = true,
        AllowedEnvironments = ["HIL", "TrialRun"],
        RequireDualApproval = true,
        RequireSelfHostedHilRunner = true
    };

    private static IConfiguration Configuration(bool enabled)
    {
        var values = new Dictionary<string, string?>
        {
            ["HilVerification:Enabled"] = enabled ? "true" : "false",
            ["HilVerification:RequireDualApproval"] = "true",
            ["HilVerification:RequireSelfHostedHilRunner"] = "true",
            ["HilVerification:AllowedEnvironments:0"] = "HIL",
            ["HilVerification:AllowedEnvironments:1"] = "TrialRun"
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Wcs.Host.Tests";
        public string ContentRootPath { get; set; } = "/tmp";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
