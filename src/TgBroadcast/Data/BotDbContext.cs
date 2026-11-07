using Microsoft.EntityFrameworkCore;

namespace TgBroadcast.Data;

public sealed class BotDbContext(DbContextOptions<BotDbContext> options) : DbContext(options)
{
    public DbSet<Subscriber> Subscribers => Set<Subscriber>();
    public DbSet<Broadcast> Broadcasts => Set<Broadcast>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<ReceiverState> ReceiverStates => Set<ReceiverState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Subscriber>().HasKey(x => x.ChatId);
        modelBuilder.Entity<Subscriber>().Property(x => x.ChatId).ValueGeneratedNever();
        modelBuilder.Entity<Broadcast>().HasIndex(x => x.SourceKey).IsUnique();
        modelBuilder.Entity<Broadcast>().HasIndex(x => new { x.Status, x.UpdatedAtUtc });
        modelBuilder.Entity<Broadcast>().Property(x => x.SourceKey).HasMaxLength(200);
        modelBuilder.Entity<Delivery>().HasKey(x => new { x.BroadcastId, x.ChatId });
        modelBuilder.Entity<Delivery>().HasIndex(x => new { x.Status, x.NextAttemptAtUtc });
        modelBuilder.Entity<Delivery>().HasOne(x => x.Broadcast).WithMany().HasForeignKey(x => x.BroadcastId);
        modelBuilder.Entity<Delivery>().Property(x => x.LastError).HasMaxLength(100);
        modelBuilder.Entity<ReceiverState>().Property(x => x.Id).ValueGeneratedNever();
    }
}
