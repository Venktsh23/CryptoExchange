using Microsoft.EntityFrameworkCore;
using Exchange.Core.Models;
using Exchange.Core.Persistence.Entities;
using Confluent.Kafka.Admin;

namespace Exchange.Core.Persistence.Repositories;

public class TradeRepository
{
    private readonly ExchangeDbContext _context;

    public TradeRepository(ExchangeDbContext context)
    {
        _context = context;
    }

    public async Task SaveTradeAsync(Trade trade)
    {
        var entity = new TradeEntity
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
        };

        _context.Trades.Add(entity);
        await _context.SaveChangesAsync();
    }

    public async Task SaveTradesBatchAsync(IEnumerable<Trade> trades)
    {
        // Batch insert — more efficient than one-by-one
        // The worker accumulates trades and saves in batches
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
        await _context.SaveChangesAsync();
    }

    // Get total filled quantity for a set of order IDs
   public async Task<Dictionary<Guid, decimal>> GetFilledQuantitiesAsync(
    HashSet<Guid> orderIds)
    {
        var trades = await _context.Trades
            .Where(t => orderIds.Contains(t.BuyOrderId) ||
                        orderIds.Contains(t.SellOrderId))
            .Select(t => new
            {
                t.BuyOrderId,
                t.SellOrderId,
                t.Quantity
            })
            .ToListAsync();

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
        return await _context.Trades
            .Where(t => t.TradingPair == tradingPair)
            .OrderByDescending(t => t.ExecutedAt)
            .Take(count)
            .ToListAsync();
    }
}