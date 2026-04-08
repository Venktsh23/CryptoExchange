using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Confluent.Kafka;
using Polly.CircuitBreaker;
using Exchange.Core.Persistence;
using Exchange.Core.Kafka;
using Exchange.Core.Persistence.Entities;
using Exchange.Core.Persistence.Repositories;
using Exchange.Core.Resilience;
using Polly;

namespace Exchange.Core.Engine;

public class OutboxPublisherService : BackgroundService
{
    private readonly IServiceScopeFactory            _scopeFactory;
    private readonly ILogger<OutboxPublisherService> _logger;
    private readonly KafkaSettings                   _kafkaSettings;
    private readonly ResiliencePipeline              _kafkaPipeline;

    private readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(500);
    private const int BatchSize      = 50;
    private const int MaxRetryCount  = 5;
    private long _totalPublished     = 0;

    public OutboxPublisherService(
        IServiceScopeFactory scopeFactory,
        ILogger<OutboxPublisherService> logger,
        KafkaSettings kafkaSettings)
    {
        _scopeFactory  = scopeFactory;
        _logger        = logger;
        _kafkaSettings = kafkaSettings;
        _kafkaPipeline = ResiliencePipelineFactory
            .CreateKafkaPipeline(logger, "OutboxKafka");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Outbox Publisher started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishPendingMessagesAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Outbox publisher cycle error — will retry");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    private async Task PublishPendingMessagesAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExchangeDbContext>();

        var pending = await db.OutboxMessages
            .Where(m => m.PublishedAt == null && m.RetryCount < MaxRetryCount)
            .OrderBy(m => m.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (!pending.Any()) return;

        _logger.LogInformation(
            "Outbox: found {Count} unpublished messages — attempting publish",
            pending.Count);

        using var producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers      = _kafkaSettings.BootstrapServers,
            Acks                  = Acks.All,
            EnableIdempotence     = true,
            MessageSendMaxRetries = MaxRetryCount,
            MessageTimeoutMs      = 5000
        }).Build();

        foreach (var message in pending)
        {
            try
            {
                await _kafkaPipeline.ExecuteAsync(async ct =>
                    await producer.ProduceAsync(
                        message.Topic,
                        new Message<string, string>
                        {
                            Key   = message.MessageKey,
                            Value = message.Payload
                        },
                        ct),
                    ct);

                message.PublishedAt = DateTime.UtcNow;
                _totalPublished++;

                _logger.LogInformation(
                    "Outbox published | OrderId: {Key} | Type: {Type}",
                    message.MessageKey, message.MessageType);
            }
            catch (BrokenCircuitException)
            {
                // Kafka is confirmed down — circuit is open
                // Stop processing the batch entirely
                // Don't increment RetryCount — these messages aren't at fault
                // They'll be picked up again when the circuit closes
                _logger.LogWarning(
                    "Outbox: Kafka circuit OPEN — halting batch, " +
                    "{Remaining} messages held in outbox",
                    pending.Count(m => m.PublishedAt == null));
                break;
            }
            catch (Exception ex)
            {
                message.RetryCount++;
                message.LastError = ex.Message;

                _logger.LogWarning(
                    "Outbox publish failed (attempt {Attempt}/{Max}) | Error: {Error}",
                    message.RetryCount, MaxRetryCount, ex.Message);

                if (message.RetryCount >= MaxRetryCount)
                {
                    _logger.LogError(
                        "Outbox message {Id} exceeded max retries — releasing fund lock",
                        message.Id);
                    await ReleaseFundsForDeadMessagesAsync(message, scope);
                }
            }
        }

        producer.Flush(TimeSpan.FromSeconds(5));
        await db.SaveChangesAsync(ct);
    }

    private async Task ReleaseFundsForDeadMessagesAsync(
        OutboxMessageEntity message,
        IServiceScope scope)
    {
        try
        {
            var orderMsg = System.Text.Json.JsonSerializer
                .Deserialize<OrderMessage>(message.Payload);

            if (orderMsg == null)
            {
                _logger.LogError(
                    "Cannot release funds — failed to deserialize payload for outbox {Id}",
                    message.Id);
                return;
            }

            var accountRepo = scope.ServiceProvider
                .GetRequiredService<AccountRepository>();

            await accountRepo.ReleaseFundsAsync(orderMsg.Id);

            _logger.LogInformation(
                "FUNDS RELEASED | OrderId: {OrderId} | Reason: outbox max retries exceeded",
                orderMsg.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "CRITICAL: Fund release failed for outbox {Id} — manual intervention required",
                message.Id);
        }
    }
}