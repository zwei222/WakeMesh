using Microsoft.EntityFrameworkCore;

namespace WakeMesh.Api.Data;

public sealed class WakeMeshDbContext : DbContext
{
    public WakeMeshDbContext(DbContextOptions<WakeMeshDbContext> options) : base(options)
    {
    }

    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();
    public DbSet<AlarmGroupEntity> AlarmGroups => Set<AlarmGroupEntity>();
    public DbSet<DeviceScheduleEntity> DeviceSchedules => Set<DeviceScheduleEntity>();
    public DbSet<DesiredTombstoneEntity> Tombstones => Set<DesiredTombstoneEntity>();
    public DbSet<SnapshotReceiptEntity> SnapshotReceipts => Set<SnapshotReceiptEntity>();
    public DbSet<AckRecordEntity> AckRecords => Set<AckRecordEntity>();
    public DbSet<PushOutboxEntity> PushOutbox => Set<PushOutboxEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeviceEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.CredentialHash).IsRequired();
            e.Property(x => x.CancelConfirmed).IsRequired();
            e.Property(x => x.PushToken).HasMaxLength(4096);
            e.Property(x => x.PushNotificationsCapable).IsRequired();
            e.Property(x => x.RowVersion).IsConcurrencyToken();
        });

        modelBuilder.Entity<PushOutboxEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.MessageType).HasMaxLength(64).IsRequired();
            e.Property(x => x.Status).HasMaxLength(32).IsRequired();
            e.Property(x => x.LastErrorCode).HasMaxLength(64);
            e.Property(x => x.LeaseOwner).HasMaxLength(64);
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasIndex(x => new { x.DeviceId, x.DesiredRevision, x.Status });
            e.HasIndex(x => new { x.Status, x.LeaseExpiresAt });
        });

        modelBuilder.Entity<AlarmGroupEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Label).HasMaxLength(200).IsRequired();
            e.Property(x => x.Repeat).HasMaxLength(16).IsRequired();
            e.Property(x => x.TimeZone).HasMaxLength(64).IsRequired();
            e.Property(x => x.RowVersion).IsConcurrencyToken();
            e.HasMany(x => x.Targets)
                .WithOne(t => t.Group!)
                .HasForeignKey(t => t.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeviceScheduleEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GroupId, x.DeviceId }).IsUnique();
            e.Property(x => x.Time).HasMaxLength(8).IsRequired();
            e.Property(x => x.SoundKey).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<DesiredTombstoneEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.DeviceId, x.ScheduleId, x.Cleared });
        });

        modelBuilder.Entity<SnapshotReceiptEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.DeviceId, x.Revision });
        });

        modelBuilder.Entity<AckRecordEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.DeviceId, x.ReceiptId, x.Revision });
        });
    }
}
