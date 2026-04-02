using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Exchange.Core.Models;

namespace Exchange.Core.Kafka;

public class OrderProducer : IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly KafkaSettings             _settings;
    private readonly ILogger<OrderProducer>    _logger;

    public OrderProducer(KafkaSettings settings, ILogger<OrderProducer> logger)
    {
        _settings = settings;
        _logger   = logger;

        var config = new ProducerConfig
        {
            BootstrapServers = settings.BootstrapServers,

            // Acks.All = wait for Kafka to confirm written to disk
            // before returning — strongest durability guarantee
            Acks = Acks.All,

            // Prevent duplicate messages if producer retries
            // after a network blip
            EnableIdempotence = true,

            // Retry up to 5 times on transient failures
            MessageSendMaxRetries = 5,

            // Wait up to 3 minutes for Kafka to confirm
            MessageTimeoutMs = 180_000
        };

        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public async Task PublishOrderAsync(Order order)
    {
        var message = new OrderMessage
        {
            Id          = order.Id,
            UserId      = order.UserId,
            TradingPair = order.TradingPair,
            Side        = order.Side.ToString(),
            Price       = order.Price,
            Quantity    = order.Quantity,
            CreatedAt   = order.CreatedAt
        };

        var json = JsonSerializer.Serialize(message);

        // Key = TradingPair
        // All BTC/USD orders go to the same partition
        // Guarantees price-time ordering within a trading pair
        var kafkaMessage = new Message<string, string>
        {
            Key   = order.TradingPair,
            Value = json
        };

        try
        {
            var result = await _producer.ProduceAsync(
                _settings.OrdersTopic, kafkaMessage);

            _logger.LogDebug(
                "Order published | {Pair} | {Side} | {Qty}@{Price} | " +
                "Offset: {Offset}",
                order.TradingPair, order.Side,
                order.Quantity, order.Price,
                result.Offset.Value);
        }
        catch (ProduceException<string, string> ex)
        {
            _logger.LogError(ex,
                "Failed to publish order {OrderId} to Kafka",
                order.Id);
            throw;
        }
    }

    public void Dispose()
    {
        // Flush sends any buffered messages before shutdown
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
    }
}