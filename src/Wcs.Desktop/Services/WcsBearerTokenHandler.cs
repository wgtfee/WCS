using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Wcs.Desktop.Models;

namespace Wcs.Desktop.Services;

/// <summary>
/// Adds the current desktop IAM/local bearer token to every WCS typed HttpClient request.
/// This keeps the existing API services unaware of the authentication mechanism while
/// allowing Centralized mode to authorize the same calls through WCS Host.
/// </summary>
public sealed class WcsBearerTokenHandler : DelegatingHandler
{
    private readonly IAuthState _authState;
    private readonly IDesktopIamAuthService _iamAuth;
    private readonly DesktopIamOptions _iamOptions;
    private readonly WcsDesktopOptions _desktopOptions;

    public WcsBearerTokenHandler(
        IAuthState authState,
        IDesktopIamAuthService iamAuth,
        IOptions<DesktopIamOptions> iamOptions,
        IOptions<WcsDesktopOptions> desktopOptions)
    {
        _authState = authState;
        _iamAuth = iamAuth;
        _iamOptions = iamOptions.Value;
        _desktopOptions = desktopOptions.Value;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = _iamOptions.Enabled
            ? await _iamAuth.GetAccessTokenAsync(cancellationToken)
            : _authState.Token;
        if (!string.IsNullOrWhiteSpace(token) && IsGatewayRequest(request.RequestUri))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await base.SendAsync(request, cancellationToken);
    }

    private bool IsGatewayRequest(Uri? requestUri)
        => requestUri is { IsAbsoluteUri: true }
            && Uri.TryCreate(_desktopOptions.ServerUrl, UriKind.Absolute, out var gateway)
            && string.Equals(requestUri.Scheme, gateway.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(requestUri.Host, gateway.Host, StringComparison.OrdinalIgnoreCase)
            && requestUri.Port == gateway.Port;
}
