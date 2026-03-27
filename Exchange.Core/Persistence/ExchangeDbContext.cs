using Microsoft.EntityFrameworkCore;
using Exchange.Core.Persistence.Entities;

namespace Exchange.Core.Persistence;

public class ExchangeDbContext : DbContext
{
    public ExchangeDbContext(DbContextOptions<ExchangeDbContext> options)
        : base(options) { }

    public DbSet<TradeEntity> Trades => Set<TradeEntity>();

    public DbSet<OrderBookSnapshotEntity> OrderBookSnapshots => Set<OrderBookSnapshotEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TradeEntity>(entity =>
        {
            entity.HasKey(t => t.Id);

            // Index on TradingPair — most queries filter by pair
            entity.HasIndex(t => t.TradingPair);

            // Index on ExecutedAt — for time-range queries
            entity.HasIndex(t => t.ExecutedAt);

            // Price precision — 18 digits, 8 decimal places
            // Handles both tiny altcoins and large BTC values
            entity.Property(t => t.Price)
                  .HasPrecision(18, 8);

            entity.Property(t => t.Quantity)
                  .HasPrecision(18, 8);

            entity.Property(t => t.TotalValue)
                  .HasPrecision(18, 8);
        });

        modelBuilder.Entity<OrderBookSnapshotEntity>(entity =>
{
    entity.HasKey(s => s.Id);
    entity.HasIndex(s => new { s.TradingPair, s.CreatedAt });

    // Only keep latest 12 snapshots per pair (1 hour of history at 5min intervals)
    // Older ones pruned by the snapshot service
});
    }
}