using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Exchange.Core.Models;

namespace Exchange.Core.Persistence.Repositories;

public class SnapshotRepository
{
    private readonly ExchangeDbContext _context;

    public SnapshotRepository(ExchangeDbContext context)
    {
        _context = context;
    }

    public async Task<OrderBookSnapshot?> GetLatestSnapshotAsync(
        string tradingPair)
    {
        var entity = await _context.OrderBookSnapshots
            .Where(s => s.TradingPair == tradingPair)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync();

        if (entity == null) return null;

        return DeserializeSnapshot(entity.SnapshotJson, tradingPair);
    }

    public async Task<List<string>> GetSnapshotTradingPairsAsync()
    {
        return await _context.OrderBookSnapshots
            .Select(s => s.TradingPair)
            .Distinct()
            .ToListAsync();
    }

    private static OrderBookSnapshot? DeserializeSnapshot(
        string json, string tradingPair)
    {
        try
        {
            var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new OrderBookSnapshot
            {
                TradingPair = tradingPair,
                Bids        = DeserializeOrders(root, "bids"),
                Asks        = DeserializeOrders(root, "asks")
            };
        }
        catch
        {
            return null;
        }
    }

    private static List<Order> DeserializeOrders(
        JsonElement root, string side)
    {
        var orders = new List<Order>();

        if (!root.TryGetProperty(side, out var sideElement))
            return orders;

        foreach (var priceLevel in sideElement.EnumerateObject())
        {
            foreach (var o in priceLevel.Value.EnumerateArray())
            {
                try
                {
                    orders.Add(new Order
                    {
                        Id             = o.GetProperty("Id").GetGuid(),
                        UserId         = o.GetProperty("UserId").GetString()!,
                        TradingPair    = o.GetProperty("TradingPair").GetString()!,
                        Side           = Enum.Parse<OrderSide>(
                                           o.GetProperty("Side").GetString()!),
                        Price          = o.GetProperty("Price").GetDecimal(),
                        Quantity       = o.GetProperty("Quantity").GetDecimal(),
                        FilledQuantity = o.GetProperty("FilledQuantity").GetDecimal(),
                        Status         = Enum.Parse<OrderStatus>(
                                           o.GetProperty("Status").GetString()!),
                        CreatedAt      = o.GetProperty("CreatedAt").GetDateTime()
                    });
                }
                catch { continue; }
            }
        }

        return orders;
    }
}

public class OrderBookSnapshot
{
    public string      TradingPair { get; set; } = string.Empty;
    public List<Order> Bids        { get; set; } = new();
    public List<Order> Asks        { get; set; } = new();
}