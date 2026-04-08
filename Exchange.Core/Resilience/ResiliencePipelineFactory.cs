using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Microsoft.Extensions.Logging;

namespace Exchange.Core.Resilience;

public static class ResiliencePipelineFactory
{
    // Full pipeline for DB operations: Bulkhead → Circuit Breaker → Retry
    // Order is intentional — outermost executes first
    // Bulkhead caps concurrency before circuit breaker even sees the request
    // Circuit breaker stops retrying a dead DB
    // Retry handles transient blips
    public static ResiliencePipeline CreateDatabasePipeline(
        ILogger logger,
        string  pipelineName)
    {
        return new ResiliencePipelineBuilder()

            .AddConcurrencyLimiter(
                permitLimit: 10,  // max 10 concurrent DB ops
                queueLimit:  20)  // max 20 waiting — beyond this, reject immediately

            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio      = 0.5,                       // open at 50% failure rate
                MinimumThroughput = 5,                         // need at least 5 requests in window
                SamplingDuration  = TimeSpan.FromSeconds(10),
                BreakDuration     = TimeSpan.FromSeconds(30),  // stay open 30s before retrying

                ShouldHandle = new PredicateBuilder()
                    .Handle<Exception>(ex =>
                        // Business logic errors don't indicate DB failure
                        // Only infrastructure exceptions should trip the breaker
                        ex is not ArgumentException &&
                        ex is not InvalidOperationException),

                OnOpened = args =>
                {
                    logger.LogWarning(
                        "[{Pipeline}] Circuit OPENED — fast-failing for {Duration}s | Reason: {Reason}",
                        pipelineName,
                        args.BreakDuration.TotalSeconds,
                        args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    logger.LogInformation(
                        "[{Pipeline}] Circuit CLOSED — DB recovered",
                        pipelineName);
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = args =>
                {
                    logger.LogInformation(
                        "[{Pipeline}] Circuit HALF-OPEN — sending one test request",
                        pipelineName);
                    return ValueTask.CompletedTask;
                }
            })

            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<Exception>(ex =>
                        ex is not ArgumentException &&
                        ex is not InvalidOperationException &&
                        ex is not BrokenCircuitException),  // don't retry an open circuit

                MaxRetryAttempts = 5,
                DelayGenerator   = args => ValueTask.FromResult<TimeSpan?>(
                    TimeSpan.FromSeconds(Math.Pow(2, args.AttemptNumber + 1))),

                OnRetry = args =>
                {
                    logger.LogWarning(
                        "[{Pipeline}] Retry {Attempt}/5 after {Delay}s | Error: {Error}",
                        pipelineName,
                        args.AttemptNumber + 1,
                        Math.Pow(2, args.AttemptNumber + 1),
                        args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                }
            })

            .Build();
    }

    // Lighter pipeline for Kafka — no bulkhead (Kafka handles its own concurrency)
    // Opens faster because Kafka outages are usually sustained, not transient
    public static ResiliencePipeline CreateKafkaPipeline(
        ILogger logger,
        string  pipelineName)
    {
        return new ResiliencePipelineBuilder()

            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio      = 0.3,                      // opens at 30% failure rate
                MinimumThroughput = 3,
                SamplingDuration  = TimeSpan.FromSeconds(5),
                BreakDuration     = TimeSpan.FromSeconds(15),

                OnOpened = args =>
                {
                    logger.LogWarning(
                        "[{Pipeline}] Kafka circuit OPENED — messages accumulating in Outbox",
                        pipelineName);
                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    logger.LogInformation(
                        "[{Pipeline}] Kafka circuit CLOSED — publishing resumed",
                        pipelineName);
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = args =>
                {
                    logger.LogInformation(
                        "[{Pipeline}] Kafka circuit HALF-OPEN — testing one message",
                        pipelineName);
                    return ValueTask.CompletedTask;
                }
            })

            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<Exception>(ex =>
                        ex is not BrokenCircuitException),

                MaxRetryAttempts = 3,
                DelayGenerator   = args => ValueTask.FromResult<TimeSpan?>(
                    TimeSpan.FromSeconds(args.AttemptNumber + 1)),

                OnRetry = args =>
                {
                    logger.LogWarning(
                        "[{Pipeline}] Kafka retry {Attempt}/3 | Error: {Error}",
                        pipelineName,
                        args.AttemptNumber + 1,
                        args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                }
            })

            .Build();
    }
}