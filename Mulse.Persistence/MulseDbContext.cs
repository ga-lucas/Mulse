using Microsoft.EntityFrameworkCore;

namespace Mulse.Persistence;

public abstract class MulseDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<WorkspaceRecord> Workspaces => Set<WorkspaceRecord>();
    public DbSet<FlowRecord> Flows => Set<FlowRecord>();
    public DbSet<FlowRevisionRecord> FlowRevisions => Set<FlowRevisionRecord>();
    public DbSet<ConfigurationRecord> ConfigurationValues => Set<ConfigurationRecord>();
    public DbSet<ModulePackageRecord> ModulePackages => Set<ModulePackageRecord>();
    public DbSet<CheckpointRecord> Checkpoints => Set<CheckpointRecord>();
    public DbSet<RetryRecord> Retries => Set<RetryRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var workspace = model.Entity<WorkspaceRecord>();
        workspace.ToTable("Workspaces");
        workspace.HasKey(row => row.Id);
        workspace.Property(row => row.Id).ValueGeneratedNever();
        workspace.Property(row => row.Version).IsConcurrencyToken();
        workspace.Property(row => row.KeyId).HasMaxLength(128);

        var flow = model.Entity<FlowRecord>();
        flow.ToTable("Flows");
        flow.HasKey(row => row.Key);
        flow.Property(row => row.Key).HasMaxLength(64).IsUnicode(false);
        flow.HasOne(row => row.CurrentRevision).WithMany().HasForeignKey(row => row.CurrentRevisionId).OnDelete(DeleteBehavior.Restrict);

        var revision = model.Entity<FlowRevisionRecord>();
        revision.ToTable("FlowRevisions");
        revision.HasKey(row => row.Id);
        revision.Property(row => row.Id).ValueGeneratedNever();
        revision.Property(row => row.FlowKey).HasMaxLength(64).IsUnicode(false);
        revision.HasIndex(row => new { row.FlowKey, row.Number }).IsUnique();

        Payload<ConfigurationRecord>(model, "ConfigurationValues");
        Payload<ModulePackageRecord>(model, "ModulePackages");
        Payload<CheckpointRecord>(model, "Checkpoints");
        Payload<RetryRecord>(model, "Retries");
        model.Entity<CheckpointRecord>().Property(row => row.FlowKey).HasMaxLength(64).IsUnicode(false);
        model.Entity<CheckpointRecord>().HasIndex(row => row.FlowKey);
        var retry = model.Entity<RetryRecord>();
        retry.Property(row => row.FlowKey).HasMaxLength(64).IsUnicode(false);
        retry.Property(row => row.ExecutionKey).HasMaxLength(64).IsUnicode(false);
        retry.HasIndex(row => new { row.FlowKey, row.ExecutionKey });
        retry.HasIndex(row => new { row.NotificationsExhausted, row.NextAttemptUtcTicks });
    }

    private static void Payload<T>(ModelBuilder model, string table) where T : class, IPayloadRecord
    {
        var entity = model.Entity<T>();
        entity.ToTable(table);
        entity.HasKey(row => row.Key);
        entity.Property(row => row.Key).HasMaxLength(64).IsUnicode(false);
    }
}

public sealed class MulseDbContextFactory<T>(IDbContextFactory<T> factory) : IDbContextFactory<MulseDbContext> where T : MulseDbContext
{
    public MulseDbContext CreateDbContext() => factory.CreateDbContext();
    public async Task<MulseDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
}
