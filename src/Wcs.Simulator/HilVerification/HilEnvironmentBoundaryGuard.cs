namespace Wcs.Simulator.HilVerification;

public sealed record HilEnvironmentAccessDecision(bool Allowed, string Reason);

/// <summary>
/// Fail-closed environment boundary for S9 inspection/readiness surfaces.
/// Production is never eligible, even if a configuration mistake tries to enable it.
/// </summary>
public static class HilEnvironmentBoundaryGuard
{
    public static HilEnvironmentAccessDecision Evaluate(string? environmentName, HilVerificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            options.Validate();
        }
        catch (InvalidOperationException exception)
        {
            return new(false, $"Invalid HIL verification configuration: {exception.Message}");
        }

        if (!options.Enabled)
            return new(false, "HilVerification is disabled.");
        if (string.IsNullOrWhiteSpace(environmentName))
            return new(false, "Host environment name is missing.");
        if (string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase))
            return new(false, "Production is fail-closed for S9 verification APIs.");
        if (!options.AllowedEnvironments.Contains(environmentName, StringComparer.OrdinalIgnoreCase))
            return new(false, $"Environment '{environmentName}' is not approved for S9 verification APIs.");

        return new(true, "S9 read-only verification surface is enabled for the approved non-production environment.");
    }
}
