using StackExchange.Redis;
using Microsoft.Extensions.Logging;

namespace Exchange.Core.RateLimit;

public class RedisRateLimiter
{
    private readonly IConnectionMultiplexer  _redis;
    private readonly RateLimitSettings       _settings;
    private readonly ILogger<RedisRateLimiter> _logger;

    public RedisRateLimiter(
        IConnectionMultiplexer redis,
        RateLimitSettings settings,
        ILogger<RedisRateLimiter> logger)
    {
        _redis    = redis;
        _settings = settings;
        _logger   = logger;
    }

    // Returns true if request is allowed
    // Returns false if rate limit exceeded
    public async Task<RateLimitResult> IsAllowedAsync(string userId)
    {
        var db  = _redis.GetDatabase();
        var key = $"ratelimit:orders:{userId}";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var windowStart = now - _settings.WindowMs;

        // Lua script — runs atomically in Redis
        // No race condition possible between check and increment
        // All operations execute as a single unit
        var script = @"
            -- Remove requests outside the current window
            redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[1])
            
            -- Count requests in current window
            local count = redis.call('ZCARD', KEYS[1])
            
            -- Check if limit exceeded
            if count >= tonumber(ARGV[3]) then
                -- Get oldest request time to calculate retry-after
                local oldest = redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')
                local retryAfter = 0
                if #oldest > 0 then
                    retryAfter = tonumber(oldest[2]) + tonumber(ARGV[4]) - tonumber(ARGV[2])
                end
                return {0, count, retryAfter}
            end
            
            -- Add current request with timestamp as score
            -- Member = timestamp:random to ensure uniqueness
            redis.call('ZADD', KEYS[1], ARGV[2], ARGV[2] .. ':' .. ARGV[5])
            
            -- Set key expiry — auto-cleanup after window expires
            redis.call('PEXPIRE', KEYS[1], ARGV[4])
            
            -- Return allowed with current count
            local newCount = redis.call('ZCARD', KEYS[1])
            return {1, newCount, 0}
        ";

        try
        {
            var result = (RedisValue[]) await db.ScriptEvaluateAsync(
                script,
                keys:   new RedisKey[]   { key },
                values: new RedisValue[]
                {
                    windowStart,                          // ARGV[1] window start
                    now,                                  // ARGV[2] current time
                    _settings.OrdersPerSecond,            // ARGV[3] limit
                    _settings.WindowMs,                   // ARGV[4] window duration
                    Guid.NewGuid().ToString("N")[..8]     // ARGV[5] unique suffix
                });

            var allowed    = (int)result[0] == 1;
            var count      = (int)result[1];
            var retryAfter = (long)result[2];

            if (!allowed)
            {
                _logger.LogWarning(
                    "RATE LIMIT | User: {User} | " +
                    "Count: {Count}/{Limit} | " +
                    "RetryAfter: {RetryAfter}ms",
                    userId, count,
                    _settings.OrdersPerSecond,
                    retryAfter);
            }

            return new RateLimitResult
            {
                IsAllowed    = allowed,
                CurrentCount = count,
                RetryAfterMs = retryAfter
            };
        }
        catch (Exception ex)
        {
            // Redis is down — fail open (allow the request)
            // Better to allow some extra requests than to
            // reject all requests because Redis is unavailable
            _logger.LogError(ex,
                "Redis rate limiter unavailable — failing open for user {User}",
                userId);

            return new RateLimitResult { IsAllowed = true, CurrentCount = 0 };
        }
    }
}

public class RateLimitResult
{
    public bool IsAllowed    { get; set; }
    public int  CurrentCount { get; set; }
    public long RetryAfterMs { get; set; }
}