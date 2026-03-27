using Microsoft.EntityFrameworkCore;
using Exchange.Core.Models;
using Exchange.Core.Persistence.Entities;

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