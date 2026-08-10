using Microsoft.Extensions.Options;

namespace Wcs.Desktop.Services;

/// <summary>Rewrites every WCS Desktop business request onto the Gateway's WCS routes.</summary>
public sealed class WcsGatewayPathHandler(IOptions<WcsDesktopOptions> options) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { IsAbsoluteUri: true } uri
            && Uri.TryCreate(options.Value.ServerUrl, UriKind.Absolute, out var gateway)
            && SameOrigin(uri, gateway))
        {
            var apiPrefix = "/" + options.Value.ApiPrefix.Trim('/');
            var path = uri.AbsolutePath;
            var rewritten = path.Equals(apiPrefix, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(apiPrefix + "/", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/wcs", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/wcs/", StringComparison.OrdinalIgnoreCase)
                    ? path
                : path.Equals("/api", StringComparison.OrdinalIgnoreCase)
                ? apiPrefix
                : path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                    ? apiPrefix + path[4..]
                    : "/wcs/" + path.TrimStart('/');
            var builder = new UriBuilder(uri) { Path = rewritten };
            request.RequestUri = builder.Uri;
        }
        return base.SendAsync(request, cancellationToken);
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;
}
