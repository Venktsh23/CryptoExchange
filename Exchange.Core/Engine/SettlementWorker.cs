using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Exchange.Core.Models;
using Exchange.Core.Persistence.Repositories;

namespace Exchange.Core.Engine;

public class SettlementWorker : BackgroundService
{
    private readonly SettlementChannel _settlementChannel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SettlementWorker> _logger;

    // Batch settings — saves every 100 trades OR every 2 seconds
    // whichever comes first
    private const int BatchSize        = 100;
    private const int BatchTimeoutMs   = 2000;

    private long _totalSaved  = 0;
    private long _totalBatches = 0;

    public SettlementWorker(
        SettlementChannel settlementChannel,
        IServiceScopeFactory scopeFactory,
        ILogger<SettlementWorker> logger)
    {
        _settlementChannel = settlementChannel;
        _scopeFactory      = scopeFactory;
        _logger            = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Settlement Worker started. Waiting for trades...");

        var batch = new List<Trade>(BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Try to fill a batch within the timeout window
                var batchDeadline = DateTime.UtcNow.AddMilliseconds(BatchTimeoutMs);

                while (batch.Count < BatchSize && DateTime.UtcNow < batchDeadline)
                {
                    // Wait up to remaining time for next trade
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
                        // Timeout reached — save whatever we have
                        break;
                    }
                }

                // Save batch if we have anything
                if (batch.Count > 0)
                {
                    await SaveBatchAsync(batch, stoppingToken);
                    batch.Clear();
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Settlement worker error — retrying in 5s");
                await Task.Delay(5000, stoppingToken);
            }
        }

        // Drain remaining trades on shutdown
        _logger.LogInformation("Settlement Worker shutting down — draining remaining trades...");
        while (_settlementChannel.Reader.TryRead(out var trade))
            batch.Add(trade);

        if (batch.Count > 0)
            await SaveBatchAsync(batch, CancellationToken.None);

        _logger.LogInformation("Settlement Worker stopped. Total saved: {Total}", _totalSaved);
    }

    private async Task SaveBatchAsync(List<Trade> batch, CancellationToken ct)
    {
        // IServiceScopeFactory creates a fresh DbContext per batch
        // DbContext is not thread-safe — never share it across scopes
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<TradeRepository>();

        var sw = System.Diagnostics.Stopwatch.StartNew();

        await repo.SaveTradesBatchAsync(batch);

        sw.Stop();
        _totalSaved  += batch.Count;
        _totalBatches++;

        _logger.LogInformation(
            "SETTLEMENT | Batch #{Batch} | Saved {Count} trades in {Ms}ms | " +
            "Total persisted: {Total}",
            _totalBatches, batch.Count, sw.ElapsedMilliseconds, _totalSaved
        );
    }
}