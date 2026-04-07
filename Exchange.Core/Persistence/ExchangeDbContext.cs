using Microsoft.EntityFrameworkCore;
using Exchange.Core.Persistence.Entities;

namespace Exchange.Core.Persistence;

public class ExchangeDbContext : DbContext
{
    public ExchangeDbContext(DbContextOptions<ExchangeDbContext> options)
        : base(options) { }

    public DbSet<TradeEntity>              Trades              => Set<TradeEntity>();
    public DbSet<OrderBookSnapshotEntity>  OrderBookSnapshots  => Set<OrderBookSnapshotEntity>();
    public DbSet<AccountEntity>            Accounts            => Set<AccountEntity>();
    public DbSet<FundLockEntity>           FundLocks           => Set<FundLockEntity>();
    public DbSet<AccountTransactionEntity> AccountTransactions => Set<AccountTransactionEntity>();

    public DbSet<OutboxMessageEntity> OutboxMessages => Set<OutboxMessageEntity>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessageEntity>(entity =>
        {
            entity.HasKey(o => o.Id);
            entity.HasIndex(o => o.PublishedAt);  // fast query for unpublished
            entity.HasIndex(o => o.CreatedAt);    // ordering
            entity.Property(o => o.Payload).HasColumnType("text");
        });

        
        // Existing trade config
        modelBuilder.Entity<TradeEntity>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => t.TradingPair);
            entity.HasIndex(t => t.ExecutedAt);
            entity.Property(t => t.Price).HasPrecision(18, 8);
            entity.Property(t => t.Quantity).HasPrecision(18, 8);
            entity.Property(t => t.TotalValue).HasPrecision(18, 8);
        });

        // Existing snapshot config
        modelBuilder.Entity<OrderBookSnapshotEntity>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => new { s.TradingPair, s.CreatedAt });
        });

        // Account config
        modelBuilder.Entity<AccountEntity>(entity =>
        {
            entity.HasKey(a => a.Id);

            // One account per user per currency
            entity.HasIndex(a => new { a.UserId, a.Currency })
                  .IsUnique();

            entity.Property(a => a.TotalBalance).HasPrecision(18, 8);
            entity.Property(a => a.LockedBalance).HasPrecision(18, 8);

            // Optimistic concurrency — EF Core checks RowVersion
            // on every update. If another thread updated first,
            // EF throws DbUpdateConcurrencyException
            // entity.UseXminAsConcurrencyToken();
        });

        // FundLock config
        modelBuilder.Entity<FundLockEntity>(entity =>
        {
            entity.HasKey(f => f.Id);
            entity.HasIndex(f => f.OrderId);
            entity.HasIndex(f => new { f.UserId, f.Status });
            entity.Property(f => f.Amount).HasPrecision(18, 8);
        });

        // Transaction config
        modelBuilder.Entity<AccountTransactionEntity>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => t.UserId);
            entity.HasIndex(t => t.CreatedAt);
            entity.Property(t => t.Amount).HasPrecision(18, 8);
        });
    }
}