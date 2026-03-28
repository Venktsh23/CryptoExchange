using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Retry;
using Exchange.Core.Models;
using Exchange.Core.Persistence.Repositories;

namespace Exchange.Core.Kafka;

public class KafkaSettlementWorker : BackgroundService
{
    private readonly KafkaSettings _settings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<KafkaSettlementWorker> _logger;

    private long _totalProcessed = 0;
    private long _totalBatches   = 0;

    private readonly ResiliencePipeline _retryPipeline;

    public KafkaSettlementWorker(
        KafkaSettings settings,
        IServiceScopeFactory scopeFactory,
        ILogger<KafkaSettlementWorker> logger)
    {
        _settings     = settings;
        _scopeFactory = scopeFactory;
        _logger       = logger;

        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(),
                MaxRetryAttempts = 5,
                DelayGenerator = static args =>
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, args.AttemptNumber + 1));
                    return ValueTask.FromResult<TimeSpan?>(delay);
                },
                OnRetry = args =>
                {
                    logger.LogWarning(
                        "DB save failed (attempt {Attempt}). " +
                        "Retrying in {Delay}s. Error: {Error}",
                        args.AttemptNumber + 1,
                        Math.Pow(2, args.AttemptNumber + 1),
                        args.Outcome.Exception?.Message
                    );
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Kafka Settlement Worker started. " +
            "Topic: {Topic} | Group: {Group}",
            _settings.TradesTopic,
            _settings.ConsumerGroupId
        );

        var config = new ConsumerConfig
        {
            BootstrapServers = _settings.BootstrapServers,
            GroupId          = _settings.ConsumerGroupId,

            // StartFromBeginning — on first run, process ALL trades
            // from the beginning of the topic
            // On subsequent runs, continues from last committed offset
            AutoOffsetReset = AutoOffsetReset.Earliest,

            // CRITICAL — disable auto commit
            // We manually commit ONLY after successful DB save
            // If DB save fails, offset is not committed
            // On restart, Kafka replays from last committed position
            EnableAutoCommit = false
        };

        // Run consumer on a background thread — it blocks while polling
        await Task.Run(() => ConsumeLoop(config, stoppingToken), stoppingToken);
    }

    private void ConsumeLoop(ConsumerConfig config, CancellationToken ct)
    {
        using var consumer = new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, error) =>
                _logger.LogError("Kafka consumer error: {Error}", error.Reason))
            .Build();

        consumer.Subscribe(_settings.TradesTopic);
        _logger.LogInformation(
            "Subscribed to Kafka topic: {Topic}", _settings.TradesTopic);

        var batch = new List<TradeMessage>();
        var lastOffset = (TopicPartitionOffset?)null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Poll with 2 second timeout — collects messages into batch
                    var result = consumer.Consume(TimeSpan.FromSeconds(2));

                    if (result != null)
                    {
                        var message = JsonSerializer.Deserialize<TradeMessage>(result.Message.Value);

                        if (message != null)
                        {
                            batch.Add(message);
                            lastOffset = result.TopicPartitionOffset;

                            _logger.LogDebug(
                                "Consumed trade from Kafka | " +
                                "Offset: {Offset} | Trade: {TradeId}",
                                result.Offset.Value,
                                message.Id
                            );
                        }
                    }

                    // Save batch when full OR when poll returned nothing
                    // (timeout means no more messages right now)
                    bool batchFull    = batch.Count >= 100;
                    bool nothingLeft  = result == null && batch.Count > 0;

                    if ((batchFull || nothingLeft) && batch.Count > 0)
                    {
                        // Save to DB with Polly retry
                        _retryPipeline.Execute(() => SaveBatch(batch));

                        // Only commit offset AFTER successful DB save
                        // This is the guarantee — trade hits DB before
                        // Kafka moves forward
                        if (lastOffset != null)
                        {
                            consumer.Commit(new[] { lastOffset });

                            _logger.LogInformation(
                                "KAFKA SETTLEMENT | Batch #{N} | " +
                                "Saved {Count} trades | " +
                                "Committed offset: {Offset}",
                                ++_totalBatches,
                                batch.Count,
                                lastOffset.Offset.Value
                            );
                        }

                        _totalProcessed += batch.Count;
                        batch.Clear();
                        lastOffset = null;
                    }
                }
                catch (ConsumeException ex)
                {
                    _logger.LogError(ex,
                        "Kafka consume error — continuing: {Error}",
                        ex.Error.Reason);
                }
            }
        }
        finally
        {
            // Save any remaining batch on shutdown
            if (batch.Count > 0)
            {
                _logger.LogInformation(
                    "Shutdown — saving final batch of {Count} trades", batch.Count);
                SaveBatch(batch);

                if (lastOffset != null)
                    consumer.Commit(new[] { lastOffset });
            }

            consumer.Close();
            _logger.LogInformation(
                "Kafka Settlement Worker stopped. Total processed: {Total}",
                _totalProcessed
            );
        }
    }

    private void SaveBatch(List<TradeMessage> batch)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<TradeRepository>();

        // Map Kafka messages back to domain trades for the repository
        var trades = batch.Select(m => new Trade
        {
            Id           = m.Id,
            TradingPair  = m.TradingPair,
            BuyOrderId   = m.BuyOrderId,
            SellOrderId  = m.SellOrderId,
            BuyerUserId  = m.BuyerUserId,
            SellerUserId = m.SellerUserId,
            Price        = m.Price,
            Quantity     = m.Quantity,
            ExecutedAt   = m.ExecutedAt
        }).ToList();

        // Synchronous save inside the Polly pipeline
        repo.SaveTradesBatchAsync(trades).GetAwaiter().GetResult();
    }
}