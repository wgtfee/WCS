namespace Wcs.Infrastructure.AnomalyDetection.Fusion;

using System.Threading.Channels;
using Wcs.Core.AnomalyDetection.Fusion;

public sealed class AnomalyEvidenceChannel :
    IAnomalyEvidenceSink,
    IAnomalyEvidenceIngressStatus
{
    private readonly AnomalyFusionOptions _options;
    private readonly Channel<AnomalyEvidence> _channel;
    private long _written;
    private long _dropped;
    private long _read;

    public AnomalyEvidenceChannel(AnomalyFusionOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _channel = Channel.CreateBounded<AnomalyEvidence>(new BoundedChannelOptions(options.ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            // Wait + TryWrite 保持生产者非阻塞，并能准确得到 false 以统计丢弃。
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public ChannelReader<AnomalyEvidence> Reader => _channel.Reader;

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

    public void RecordRead() => Interlocked.Increment(ref _read);

    public AnomalyEvidenceIngressStatus GetStatus() => new()
    {
        Enabled = _options.Enabled,
        Capacity = _options.ChannelCapacity,
        Written = Interlocked.Read(ref _written),
        Dropped = Interlocked.Read(ref _dropped),
        Read = Interlocked.Read(ref _read)
    };
}
