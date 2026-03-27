using System.Threading.Channels;
using Exchange.Core.Models;

namespace Exchange.Core.Engine;

public class SettlementChannel
{
    // Larger capacity than the order channel
    // Trades accumulate here while the DB worker batches them
    private readonly Channel<Trade> _channel = Channel.CreateBounded<Trade>(
        new BoundedChannelOptions(50_000)
        {
            FullMode    = BoundedChannelFullMode.Wait,
            SingleReader = true,   // Only the settlement worker reads
            SingleWriter = false   // Engine + any other source can write
        }
    );

    public ChannelWriter<Trade> Writer => _channel.Writer;
    public ChannelReader<Trade> Reader => _channel.Reader;
}