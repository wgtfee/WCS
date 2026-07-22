namespace Wcs.Host.Middleware;

using System.Diagnostics;

public sealed class TransportTraceContextMiddleware
{
    public const string TraceHeaderName = "X-Trace-Id";
    private readonly RequestDelegate _next;
    private readonly ILogger<TransportTraceContextMiddleware> _logger;

    public TransportTraceContextMiddleware(
        RequestDelegate next,
        ILogger<TransportTraceContextMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var traceId = Activity.Current?.TraceId.ToString();
        if (string.IsNullOrWhiteSpace(traceId))
            traceId = Guid.NewGuid().ToString("N");

        context.Items[TraceHeaderName] = traceId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[TraceHeaderName] = traceId;
            return Task.CompletedTask;
        });

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["TraceId"] = traceId,
            ["RequestPath"] = context.Request.Path.Value ?? string.Empty
        }))
        {
            await _next(context).ConfigureAwait(false);
        }
    }
}
