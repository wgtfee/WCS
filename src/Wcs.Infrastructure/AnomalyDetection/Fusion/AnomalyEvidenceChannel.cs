namespace Wcs.Infrastructure.AnomalyDetection.Fusion;

using System.Threading.Channels;
using Wcs.Core.AnomalyDetection.Fusion;

public sealed class AnomalyEvidenceChannel : IAnomalyEvidenceSink
{
    private readonly AnomalyFusionOptions _options;
    private readonly Channel<AnomalyEvidence> _channel;
    private long _written;
    private long _dropped;

    public AnomalyEvidenceChannel(AnomalyFusionOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _channel = Channel.CreateBounded<AnomalyEvidence>(new BoundedChannelOptions(options.ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false
        });
    }

    public ChannelReader<AnomalyEvidence> Reader => _channel.Reader;
    public long Written => Interlocked.Read(ref _written);
    public long Dropped => Interlocked.Read(ref _dropped);

    public bool TryWrite(AnomalyEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!_options.Enabled) return false;
        if (_channel.Writer.TryWrite(evidence))
        {
            Interlocked.Increment(ref _written);
            return true;
        }
        Interlocked.Increment(ref _dropped);
        return false;
    }
}
