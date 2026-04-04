using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Exchange.Core.Models;

namespace Exchange.Core.Kafka;

public class TradeProducer : IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly KafkaSettings             _settings;
    private readonly ILogger<TradeProducer>    _logger;

    public TradeProducer(KafkaSettings settings, ILogger<TradeProducer> logger)
    {
        _settings = settings;
        _logger   = logger;

        _producer = new ProducerBuilder<string, string>(
            new ProducerConfig
            {
                BootstrapServers      = settings.BootstrapServers,
                Acks                  = Acks.All,
                EnableIdempotence     = true,
                MessageSendMaxRetries = 5,
                MessageTimeoutMs      = 180_000
            }).Build();
    }

    public async Task PublishTradeAsync(Trade trade)
    {
        var message = new TradeMessage
{
    Id           = trade.Id,
    TradingPair  = trade.TradingPair,
    BuyOrderId   = trade.BuyOrderId,   // ← must be set
    SellOrderId  = trade.SellOrderId,  // ← must be set
    BuyerUserId  = trade.BuyerUserId,
    SellerUserId = trade.SellerUserId,
    Price        = trade.Price,
    Quantity     = trade.Quantity,
    TotalValue   = trade.TotalValue,
    ExecutedAt   = trade.ExecutedAt
};

        try
        {
            var result = await _producer.ProduceAsync(
                _settings.TradesTopic,
                new Message<string, string>
                {
                    Key   = trade.TradingPair,
                    Value = JsonSerializer.Serialize(message)
                });

            _logger.LogDebug(
                "Trade published | {Pair} | {Qty}@{Price} | Offset: {Offset}",
                trade.TradingPair, trade.Quantity,
                trade.Price, result.Offset.Value);
        }
        catch (ProduceException<string, string> ex)
        {
            _logger.LogError(ex,
                "Failed to publish trade {TradeId}", trade.Id);
            throw;
        }
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
    }
}