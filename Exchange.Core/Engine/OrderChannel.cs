using System.Threading.Channels;
using Exchange.Core.Models;

namespace Exchange.Core.Engine;

public class OrderChannel
{
    // Bounded capacity — if 10,000 orders pile up, the API slows down
    // rather than crashing. Backpressure in action.
    private readonly Channel<Order> _channel = Channel.CreateBounded<Order>(
        new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.Wait,    // API waits if belt is full
            SingleReader = true,                        // Only the engine reads
            SingleWriter = false                        // Many API threads can write
        }
    );

    public ChannelWriter<Order> Writer => _channel.Writer;
    public ChannelReader<Order> Reader => _channel.Reader;
}