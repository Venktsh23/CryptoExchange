using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Exchange.Core.Models;

namespace Exchange.Core.Kafka;

public class TradeProducer : IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly KafkaSettings _settings;
    private readonly ILogger<TradeProducer> _logger;

    public TradeProducer(KafkaSettings settings, ILogger<TradeProducer> logger)
    {
        _settings = settings;
        _logger   = logger;

        var config = new ProducerConfig
        {
            BootstrapServers = settings.BootstrapServers,

            // Wait for Kafka to confirm the message is written to disk
            // before returning — guarantees durability
            Acks = Acks.All,

            // If Kafka is briefly unavailable, retry for up to 3 minutes
            MessageTimeoutMs = 180_000,

            // Retry up to 5 times on transient failures
            MessageSendMaxRetries = 5,

            // Enable idempotence — prevents duplicate messages
            // if producer retries after a network blip
            EnableIdempotence = true
        };

        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public async Task PublishTradeAsync(Trade trade)
    {
        var message = new TradeMessage
        {
            Id           = trade.Id,
            TradingPair  = trade.TradingPair,
            BuyOrderId   = trade.BuyOrderId,
            SellOrderId  = trade.SellOrderId,
            BuyerUserId  = trade.BuyerUserId,
            SellerUserId = trade.SellerUserId,
            Price        = trade.Price,
            Quantity     = trade.Quantity,
            TotalValue   = trade.TotalValue,
            ExecutedAt   = trade.ExecutedAt
        };

        var json = JsonSerializer.Serialize(message);

        // Key = TradingPair — ensures all BTC/USD trades go to the same
        // Kafka partition, guaranteeing order within a trading pair
        var kafkaMessage = new Message<string, string>
        {
            Key   = trade.TradingPair,
            Value = json
        };

        try
        {
            var result = await _producer.ProduceAsync(_settings.TradesTopic, kafkaMessage);

            _logger.LogDebug(
                "Trade published to Kafka | Topic: {Topic} | " +
                "Partition: {Partition} | Offset: {Offset}",
                result.Topic,
                result.Partition.Value,
                result.Offset.Value
            );
        }
        catch (ProduceException<string, string> ex)
        {
            _logger.LogError(ex,
                "Failed to publish trade {TradeId} to Kafka. " +
                "Trade will fall back to direct channel.",
                trade.Id
            );
            throw; // Let caller handle
        }
    }

    public void Dispose()
    {
        // Flush ensures all buffered messages are sent before shutdown
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
    }
}