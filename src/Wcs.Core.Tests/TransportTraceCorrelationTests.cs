using System.Diagnostics;
using Wcs.Core.TransportScheduling;

namespace WcsCoreTests;

public class TransportTraceCorrelationTests
{
    [Fact]
    public void Telemetry_ReusesTraceIdAcrossAsynchronousRequestStages()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TransportTelemetryNames.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        using var telemetry = new TransportTelemetryService();

        using (var enqueue = telemetry.StartOperation(
                   TransportTraceOperationKind.Dispatch,
                   "transport.queue.enqueue",
                   "TASK-CORRELATION"))
        {
            enqueue.Complete(true);
        }

        Assert.Null(Activity.Current);

        using (var command = telemetry.StartOperation(
                   TransportTraceOperationKind.PlcCommand,
                   "transport.plc.command",
                   "TASK-CORRELATION",
                   "EMS-01"))
        {
            command.Complete(true);
        }

        var traces = telemetry.GetRecentTraces(10)
            .OrderBy(x => x.CompletedAtUtc)
            .ToArray();
        Assert.Equal(2, traces.Length);
        Assert.False(string.IsNullOrWhiteSpace(traces[0].TraceId));
        Assert.Equal(traces[0].TraceId, traces[1].TraceId);
        Assert.NotEqual(traces[0].SpanId, traces[1].SpanId);
    }
}
