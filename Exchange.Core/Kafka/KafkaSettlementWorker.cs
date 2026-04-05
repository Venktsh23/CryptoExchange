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
    private readonly KafkaSettings                    _settings;
    private readonly IServiceScopeFactory             _scopeFactory;
    private readonly ILogger<KafkaSettlementWorker>   _logger;
    private readonly ResiliencePipeline               _retryPipeline;

    private long _totalSaved   = 0;
    private long _totalBatches = 0;

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
                ShouldHandle     = new PredicateBuilder().Handle<Exception>(),
                MaxRetryAttempts = 5,
                DelayGenerator   = static args =>
                    ValueTask.FromResult<TimeSpan?>(
                        TimeSpan.FromSeconds(
                            Math.Pow(2, args.AttemptNumber + 1))),
                OnRetry = args =>
                {
                    logger.LogWarning(
                        "DB save failed (attempt {Attempt}). " +
                        "Retrying in {Delay}s. Error: {Error}",
                        args.AttemptNumber + 1,
                        Math.Pow(2, args.AttemptNumber + 1),
                        args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Kafka Settlement Worker started | " +
            "Topic: {Topic} | Group: {Group}",
            _settings.TradesTopic,
            _settings.SettlementConsumerGroup);

        await Task.Run(() => ConsumeLoop(stoppingToken), stoppingToken);
    }

    private void ConsumeLoop(CancellationToken ct)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _settings.BootstrapServers,
            GroupId          = _settings.SettlementConsumerGroup,
            AutoOffsetReset  = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, e) =>
                _logger.LogError("Kafka error: {Reason}", e.Reason))
            .Build();

        consumer.Subscribe(_settings.TradesTopic);
        _logger.LogInformation(
            "Settlement subscribed to topic: {Topic}", _settings.TradesTopic);

        var batch      = new List<TradeMessage>();
        var lastResult = (ConsumeResult<string, string>?)null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = consumer.Consume(TimeSpan.FromSeconds(2));

                    if (result != null)
                    {
                        var msg = JsonSerializer
                            .Deserialize<TradeMessage>(result.Message.Value);

                        if (msg != null)
                        {
                            batch.Add(msg);
                        lastResult = result;                        }
                    }

                    // Save when batch is full OR poll returned nothing
                    bool shouldSave = batch.Count >= 100
                                   || (result == null && batch.Count > 0);

                    if (shouldSave && batch.Count > 0)
                    {
                        _retryPipeline.Execute(() => SaveBatch(batch));

                        // Commit ONLY after successful DB save
                       if (lastResult != null)
                        {
                     consumer.Commit(lastResult); // ← stores offset+1 automatically
                            _logger.LogInformation(
                            "SETTLEMENT | Batch #{N} | Saved {Count} trades | " +
                            "Offset committed: {Offset} (next read: {Next})",
                            ++_totalBatches, batch.Count,
                            lastResult.Offset.Value,
                            lastResult.Offset.Value + 1);
                        lastResult = null;
                    }

                        _totalSaved += batch.Count;
                        batch.Clear();
                        lastResult = null;
                    }
                }
                catch (ConsumeException ex)
                {
                    _logger.LogError(ex,
                        "Consume error: {Reason}", ex.Error.Reason);
                }
            }
        }
       finally
        {
            if (batch.Count > 0)
            {
                SaveBatch(batch);
                if (lastResult != null)
                    consumer.Commit(lastResult);
            }
            consumer.Close();
            _logger.LogInformation(
                "Settlement stopped. Total saved: {Total}", _totalSaved);
        }
    }

    private void SaveBatch(List<TradeMessage> batch)
{
    using var scope = _scopeFactory.CreateScope();
    var repo        = scope.ServiceProvider
        .GetRequiredService<TradeRepository>();
    var accountRepo = scope.ServiceProvider
        .GetRequiredService<AccountRepository>();

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

    // Save trades to PostgreSQL
    repo.SaveTradesBatchAsync(trades).GetAwaiter().GetResult();

    // Transfer funds for each trade
    foreach (var trade in trades)
{
    try
    {
        accountRepo.TransferTradeAsync(
    buyerUserId:  trade.BuyerUserId,
    sellerUserId: trade.SellerUserId,
    tradingPair:  trade.TradingPair,
    quantity:     trade.Quantity,
    price:        trade.Price,
    tradeId:      trade.Id,
    buyOrderId:   trade.BuyOrderId,
    sellOrderId:  trade.SellOrderId
).GetAwaiter().GetResult();

        _logger.LogInformation(
            "TRANSFER COMPLETE | {Pair} | Buyer: {Buyer} | Seller: {Seller}",
            trade.TradingPair, trade.BuyerUserId, trade.SellerUserId);
    }
    catch (Exception ex)
    {
        // Make this visible — not silent
        _logger.LogError(ex,
            "TRANSFER FAILED | Trade: {TradeId} | Error: {Error}",
            trade.Id, ex.Message);
    }
}
}


}