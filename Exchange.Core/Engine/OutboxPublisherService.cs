using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Confluent.Kafka;
using Exchange.Core.Persistence;
using Exchange.Core.Kafka;

namespace Exchange.Core.Engine;

public class OutboxPublisherService : BackgroundService
{
    private readonly IServiceScopeFactory            _scopeFactory;
    private readonly ILogger<OutboxPublisherService> _logger;
    private readonly KafkaSettings                   _kafkaSettings;

    private readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(500);
    private const int BatchSize = 50;
    private long _totalPublished = 0;

    public OutboxPublisherService(
        IServiceScopeFactory scopeFactory,
        ILogger<OutboxPublisherService> logger,
        KafkaSettings kafkaSettings)
    {
        _scopeFactory  = scopeFactory;
        _logger        = logger;
        _kafkaSettings = kafkaSettings;
        // Nothing that can throw — constructor is now safe
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
            .Where(m => m.PublishedAt == null && m.RetryCount < 5)
            .OrderBy(m => m.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (!pending.Any()) return;

        _logger.LogInformation(
            "Outbox: found {Count} unpublished messages — attempting publish",
            pending.Count);

        // Fresh producer per cycle — survives Kafka restarts cleanly
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers      = _kafkaSettings.BootstrapServers,
            Acks                  = Acks.All,
            EnableIdempotence     = true,
            MessageSendMaxRetries = 5,
            // Don't wait forever if Kafka is still down
            MessageTimeoutMs      = 5000
        }).Build();

        foreach (var message in pending)
        {
            try
            {
                await producer.ProduceAsync(
                    message.Topic,
                    new Message<string, string>
                    {
                        Key   = message.MessageKey,
                        Value = message.Payload
                    },
                    ct);

                message.PublishedAt = DateTime.UtcNow;
                _totalPublished++;

                _logger.LogInformation(
                    "Outbox published | OrderId: {Key} | Type: {Type}",
                    message.MessageKey, message.MessageType);
            }
            catch (Exception ex)
            {
                message.RetryCount++;
                message.LastError = ex.Message;

                _logger.LogWarning(
                    "Outbox publish failed (attempt {Attempt}/5) | Error: {Error}",
                    message.RetryCount, ex.Message);
            }
        }

        producer.Flush(TimeSpan.FromSeconds(5));
        await db.SaveChangesAsync(ct);
    }
}