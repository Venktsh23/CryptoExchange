using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Retry;
using Exchange.Core.Models;
using Exchange.Core.Persistence.Repositories;

namespace Exchange.Core.Engine;

public class SettlementWorker : BackgroundService
{
    private readonly SettlementChannel _settlementChannel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SettlementWorker> _logger;

    private const int BatchSize      = 100;
    private const int BatchTimeoutMs = 2000;

    private long _totalSaved   = 0;
    private long _totalBatches = 0;
    private long _totalRetries = 0;

    // Polly retry pipeline
    // If DB save fails: wait 2s, try again. Then 4s. Then 8s. Then give up and log.
    private readonly ResiliencePipeline _retryPipeline;

    public SettlementWorker(
        SettlementChannel settlementChannel,
        IServiceScopeFactory scopeFactory,
        ILogger<SettlementWorker> logger)
    {
        _settlementChannel = settlementChannel;
        _scopeFactory      = scopeFactory;
        _logger            = logger;

        // Build the Polly resilience pipeline
        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                // Retry on ANY exception — DB down, timeout, network blip
                ShouldHandle = new PredicateBuilder()
                    .Handle<Exception>(),

                MaxRetryAttempts = 5,

                // Exponential backoff: 2s, 4s, 8s, 16s, 32s
                DelayGenerator = static args =>
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, args.AttemptNumber + 1));
                    return ValueTask.FromResult<TimeSpan?>(delay);
                },

                // Log every retry attempt
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
        _logger.LogInformation("Settlement Worker started. Waiting for trades...");

        var batch = new List<Trade>(BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Fill batch within timeout window
                var batchDeadline = DateTime.UtcNow.AddMilliseconds(BatchTimeoutMs);

                while (batch.Count < BatchSize && DateTime.UtcNow < batchDeadline)
                {
                    var remaining = batchDeadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) break;

                    using var cts = CancellationTokenSource
                        .CreateLinkedTokenSource(stoppingToken);
                    cts.CancelAfter(remaining);

                    try
                    {
                        var trade = await _settlementChannel.Reader
                            .ReadAsync(cts.Token);
                        batch.Add(trade);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                if (batch.Count > 0)
                {
                    // Polly wraps the save — handles retries automatically
                    await _retryPipeline.ExecuteAsync(
                        async ct => await SaveBatchAsync(batch, ct),
                        stoppingToken
                    );

                    batch.Clear();
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // All retries exhausted — log and keep the batch for next attempt
                _logger.LogError(ex,
                    "All retry attempts exhausted for batch of {Count} trades. " +
                    "Trades remain in memory — will retry on next cycle.",
                    batch.Count
                );

                // Wait before next cycle to avoid hammering a dead DB
                await Task.Delay(10_000, stoppingToken);
            }
        }

        // Graceful shutdown — drain remaining trades
        _logger.LogInformation(
            "Settlement Worker shutting down — draining remaining trades...");

        while (_settlementChannel.Reader.TryRead(out var trade))
            batch.Add(trade);

        if (batch.Count > 0)
        {
            _logger.LogInformation(
                "Saving final batch of {Count} trades before shutdown", batch.Count);

            await _retryPipeline.ExecuteAsync(
                async ct => await SaveBatchAsync(batch, ct),
                CancellationToken.None
            );
        }

        _logger.LogInformation(
            "Settlement Worker stopped. Total saved: {Total} in {Batches} batches | " +
            "Total retries: {Retries}",
            _totalSaved, _totalBatches, _totalRetries
        );
    }

    private async Task SaveBatchAsync(List<Trade> batch, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<TradeRepository>();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await repo.SaveTradesBatchAsync(batch);
        sw.Stop();

        _totalSaved   += batch.Count;
        _totalBatches++;

        _logger.LogInformation(
            "SETTLEMENT | Batch #{Batch} | Saved {Count} trades in {Ms}ms | " +
            "Total persisted: {Total}",
            _totalBatches, batch.Count, sw.ElapsedMilliseconds, _totalSaved
        );
    }
}