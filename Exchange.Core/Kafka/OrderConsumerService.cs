using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Exchange.Core.Engine;
using Exchange.Core.Models;

namespace Exchange.Core.Kafka;

public class OrderConsumerService : BackgroundService
{
    private readonly KafkaSettings              _settings;
    private readonly MatchingEngine             _engine;
    private readonly SettlementChannel          _settlementChannel;
    private readonly ILogger<OrderConsumerService> _logger;

    private Func<Trade, Task>? _onTradeExecuted;

    private long _totalProcessed = 0;
    private long _totalTrades    = 0;

    public OrderConsumerService(
        KafkaSettings settings,
        MatchingEngine engine,
        SettlementChannel settlementChannel,
        ILogger<OrderConsumerService> logger)
    {
        _settings          = settings;
        _engine            = engine;
        _settlementChannel = settlementChannel;
        _logger            = logger;
    }

    // Called once at startup by Program.cs to wire up SignalR broadcasting
    public void SetTradeCallback(Func<Trade, Task> callback)
        => _onTradeExecuted = callback;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Order Consumer started | Topic: {Topic} | Group: {Group}",
            _settings.OrdersTopic, _settings.EngineConsumerGroup);

        // Run the blocking Kafka consume loop on a background thread
        // Kafka's consumer is synchronous — we don't want it blocking
        // the async thread pool
        await Task.Run(() => ConsumeLoop(stoppingToken), stoppingToken);
    }

    private void ConsumeLoop(CancellationToken ct)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _settings.BootstrapServers,
            GroupId          = _settings.EngineConsumerGroup,

            // On first run — start from the very beginning of the topic
            // On subsequent runs — continue from last committed offset
            AutoOffsetReset = AutoOffsetReset.Earliest,

            // CRITICAL — never auto commit
            // We commit only after the engine successfully processes the order
            // If engine crashes mid-process, Kafka replays from last commit
            EnableAutoCommit = false,

            // How long to wait for new messages before returning null
            // Allows checking cancellation token regularly
   SessionTimeoutMs = 10000,        // 10 sec
    MaxPollIntervalMs = 300000          };

        using var consumer = new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, error) =>
                _logger.LogError(
                    "Kafka consumer error: {Reason}", error.Reason))
            .SetPartitionsAssignedHandler((_, partitions) =>
                _logger.LogInformation(
                    "Partitions assigned: {Partitions}",
                    string.Join(", ", partitions)))
            .Build();

        consumer.Subscribe(_settings.OrdersTopic);

        _logger.LogInformation(
            "Subscribed to topic: {Topic}", _settings.OrdersTopic);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Poll for next message — returns null after timeout
                    var result = consumer.Consume(TimeSpan.FromSeconds(1));
                    if (result == null) continue;

                    // Deserialize the order message
                    var message = JsonSerializer
                        .Deserialize<OrderMessage>(result.Message.Value);

                    if (message == null)
                    {
                        _logger.LogWarning(
                            "Null message at offset {Offset} — skipping",
                            result.Offset.Value);

                        // Commit anyway — don't get stuck on a bad message
                        consumer.Commit(result);
                        continue;
                    }

                    // Reconstruct domain Order from Kafka message
                    var order = new Order
                    {
                        Id          = message.Id,
                        UserId      = message.UserId,
                        TradingPair = message.TradingPair,
                        Side        = Enum.Parse<OrderSide>(message.Side),
                        Price       = message.Price,
                        Quantity    = message.Quantity,
                        CreatedAt   = message.CreatedAt
                    };

                    // Process through matching engine
                    var trades = _engine.ProcessOrder(order);

                    Interlocked.Increment(ref _totalProcessed);
                    Interlocked.Add(ref _totalTrades, trades.Count);

                    // Handle each resulting trade
                    foreach (var trade in trades)
                    {
                        _logger.LogInformation(
                            "TRADE | {Pair} | {Qty}@{Price} | " +
                            "Buyer: {Buyer} | Seller: {Seller}",
                            trade.TradingPair, trade.Quantity,
                            trade.Price, trade.BuyerUserId,
                            trade.SellerUserId);

                        // Broadcast to SignalR — fire and forget
                        // Engine never waits for UI delivery
                        if (_onTradeExecuted != null)
                            _ = Task.Run(
                                () => _onTradeExecuted(trade), ct);

                        // Push to settlement channel
                        // Settlement worker saves to PostgreSQL
                        _settlementChannel.Writer
                            .TryWrite(trade); // non-blocking
                    }

                    // COMMIT only after successful processing
                    // If we crash here, Kafka replays this order on restart
                    consumer.Commit(result);

                    if (_totalProcessed % 1000 == 0)
                        _logger.LogInformation(
                            "ENGINE | Processed: {Orders} | Trades: {Trades}",
                            _totalProcessed, _totalTrades);
                }
                catch (ConsumeException ex)
                {
                    _logger.LogError(ex,
                        "Consume error — continuing: {Reason}",
                        ex.Error.Reason);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Error processing order — offset will be replayed");
                    // Don't commit — Kafka will replay this message
                    // Give Kafka a moment before retrying
                    Thread.Sleep(1000);
                }
            }
        }
        finally
        {
            consumer.Close();
            _logger.LogInformation(
                "Order Consumer stopped. " +
                "Processed: {Orders} | Trades: {Trades}",
                _totalProcessed, _totalTrades);
        }
    }
}