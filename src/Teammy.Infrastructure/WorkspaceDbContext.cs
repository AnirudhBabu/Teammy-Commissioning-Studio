using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Teammy.Infrastructure;

public abstract class WorkspaceDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<WorkspaceRow> Workspaces => Set<WorkspaceRow>();
    public DbSet<AuditRow> Audit => Set<AuditRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkspaceRow>(entity =>
        {
            entity.ToTable("workspaces");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.Property(row => row.Revision).IsConcurrencyToken();
            entity.Property(row => row.Payload).IsRequired();
        });
        modelBuilder.Entity<AuditRow>(entity =>
        {
            entity.ToTable("audit_entries");
            entity.HasKey(row => row.Revision);
            entity.Property(row => row.Revision).ValueGeneratedNever();
            entity.Property(row => row.Payload).IsRequired();
        });
    }
}

public sealed class WorkspaceRow
{
    public int Id { get; set; }
    public int Revision { get; set; }
    public required string Payload { get; set; }
}

public sealed class AuditRow
{
    public int Revision { get; set; }
    public required string Payload { get; set; }
}

public sealed class SqliteWorkspaceDbContext(DbContextOptions<SqliteWorkspaceDbContext> options) : WorkspaceDbContext(options);
public sealed class PostgresWorkspaceDbContext(DbContextOptions<PostgresWorkspaceDbContext> options) : WorkspaceDbContext(options);

// Provider-specific contexts keep SQLite and PostgreSQL migration histories honest.
public sealed class SqliteWorkspaceDbContextFactory : IDesignTimeDbContextFactory<SqliteWorkspaceDbContext>
{
    public SqliteWorkspaceDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SqliteWorkspaceDbContext>()
            .UseSqlite("Data Source=teammy-design.db").Options;
        return new SqliteWorkspaceDbContext(options);
    }
}

public sealed class PostgresWorkspaceDbContextFactory : IDesignTimeDbContextFactory<PostgresWorkspaceDbContext>
{
    public PostgresWorkspaceDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PostgresWorkspaceDbContext>()
            .UseNpgsql("Host=localhost;Database=teammy_design;Username=design").Options;
        return new PostgresWorkspaceDbContext(options);
    }
}
