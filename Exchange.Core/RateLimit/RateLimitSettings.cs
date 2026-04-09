namespace Exchange.Core.RateLimit;

public class RateLimitSettings
{
    public int OrdersPerSecond { get; set; } = 100;
    public int WindowMs        { get; set; } = 1000;
}