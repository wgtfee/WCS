using System.Net.Http.Headers;
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

    public WcsBearerTokenHandler(IAuthState authState)
    {
        _authState = authState;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = _authState.Token;
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return base.SendAsync(request, cancellationToken);
    }
}
