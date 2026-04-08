using Microsoft.EntityFrameworkCore;
using Exchange.Core.Models;
using Exchange.Core.Persistence.Entities;
using Exchange.Core.Resilience;
using Polly;
using Microsoft.Extensions.Logging;

namespace Exchange.Core.Persistence.Repositories;

public class TradeRepository
{
    private readonly ExchangeDbContext  _context;
    private readonly ResiliencePipeline _dbPipeline;
    private readonly ILogger<TradeRepository> _logger;

    public TradeRepository(
        ExchangeDbContext context,
        ILogger<TradeRepository> logger)
    {
        _context    = context;
        _logger     = logger;
        _dbPipeline = ResiliencePipelineFactory
            .CreateDatabasePipeline(logger, "TradeDB");
    }

    public async Task SaveTradeAsync(Trade trade)
    {
        _context.Trades.Add(new TradeEntity
        {
            Id           = trade.Id,
            TradingPair  = trade.TradingPair,
            BuyOrderId   = trade.BuyOrderId,
            SellOrderId  = trade.SellOrderId,
            BuyerUserId  = trade.BuyerUserId,
            SellerUserId = trade.SellerUserId,
            Price        = trade.Price,
            Quantity     = trade.Quantity,
            TotalValue   = trade.TotalValue,
            ExecutedAt   = trade.ExecutedAt,
            PersistedAt  = DateTime.UtcNow
        });

        await _dbPipeline.ExecuteAsync(async ct =>
            await _context.SaveChangesAsync(ct));
    }

    public async Task SaveTradesBatchAsync(IEnumerable<Trade> trades)
    {
        var entities = trades.Select(trade => new TradeEntity
        {
            Id           = trade.Id,
            TradingPair  = trade.TradingPair,
            BuyOrderId   = trade.BuyOrderId,
            SellOrderId  = trade.SellOrderId,
            BuyerUserId  = trade.BuyerUserId,
            SellerUserId = trade.SellerUserId,
            Price        = trade.Price,
            Quantity     = trade.Quantity,
            TotalValue   = trade.TotalValue,
            ExecutedAt   = trade.ExecutedAt,
            PersistedAt  = DateTime.UtcNow
        });

        _context.Trades.AddRange(entities);

        await _dbPipeline.ExecuteAsync(async ct =>
            await _context.SaveChangesAsync(ct));
    }

    public async Task<Dictionary<Guid, decimal>> GetFilledQuantitiesAsync(
        HashSet<Guid> orderIds)
    {
        var trades = await _dbPipeline.ExecuteAsync(async ct =>
            await _context.Trades
                .Where(t => orderIds.Contains(t.BuyOrderId) ||
                            orderIds.Contains(t.SellOrderId))
                .Select(t => new { t.BuyOrderId, t.SellOrderId, t.Quantity })
                .ToListAsync(ct));

        var result = new Dictionary<Guid, decimal>();
        foreach (var trade in trades)
        {
            if (orderIds.Contains(trade.BuyOrderId))
                result[trade.BuyOrderId] =
                    result.GetValueOrDefault(trade.BuyOrderId) + trade.Quantity;
            if (orderIds.Contains(trade.SellOrderId))
                result[trade.SellOrderId] =
                    result.GetValueOrDefault(trade.SellOrderId) + trade.Quantity;
        }

        return result;
    }

    public async Task<List<TradeEntity>> GetRecentTradesAsync(
        string tradingPair, int count = 50)
    {
        return await _dbPipeline.ExecuteAsync(async ct =>
            await _context.Trades
                .Where(t => t.TradingPair == tradingPair)
                .OrderByDescending(t => t.ExecutedAt)
                .Take(count)
                .ToListAsync(ct));
    }
}