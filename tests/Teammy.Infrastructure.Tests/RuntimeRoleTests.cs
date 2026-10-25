using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Teammy.Infrastructure;

namespace Teammy.Infrastructure.Tests;

[CollectionDefinition("Postgres runtime role", DisableParallelization = true)]
public sealed class PostgresRuntimeRoleCollection;

[Collection("Postgres runtime role")]
public sealed class RuntimeRoleTests
{
    [Fact]
    public async Task Runtime_role_provisioning_rejects_sqlite()
    {
        await using var context = new SqliteWorkspaceDbContext(new DbContextOptionsBuilder<SqliteWorkspaceDbContext>()
            .UseSqlite("Data Source=:memory:").Options);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RuntimeRoleProvisioner.ProvisionAsync(context, "not-used"));
        Assert.Contains("PostgreSQL", error.Message);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("12345678901234567890123456789012\n")]
    public async Task Invalid_passwords_are_rejected_before_a_connection_is_opened(string password)
    {
        await using var context = new PostgresWorkspaceDbContext(new DbContextOptionsBuilder<PostgresWorkspaceDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=unused;Username=unused").Options);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RuntimeRoleProvisioner.ProvisionAsync(context, password));
        Assert.Contains("32 characters", error.Message);
    }

    [PostgresRoleFact]
    public async Task Runtime_role_can_commit_audited_changes_but_cannot_mutate_history_or_schema()
    {
        await using var scope = await DatabaseScope.CreateAsync(DatabaseProvider.Postgres);
        var password = PasswordWithQuote();
        await using (var owner = scope.CreateContext())
        {
            await RuntimeRoleProvisioner.ProvisionAsync(owner, password);
        }

        await using (var runtime = scope.CreateRuntimeContext(password))
        {
            var repository = new WorkspaceRepository(runtime);
            var state = await repository.ReadAsync();
            await repository.SaveAsync(RepositoryContract.Mutation(state, "Written using restricted runtime role"), state.Revision);
            Assert.Equal(1, (await repository.ReadAsync()).Revision);
            Assert.Single(await repository.ReadAuditAsync());
        }

        string[] forbiddenStatements =
        [
            "UPDATE audit_entries SET \"Payload\" = \"Payload\" WHERE \"Revision\" = 1",
            "DELETE FROM audit_entries WHERE \"Revision\" = 1",
            "TRUNCATE TABLE audit_entries",
            "CREATE TABLE unauthorized_probe (id integer)",
            "DELETE FROM workspaces WHERE \"Id\" = 1",
            "UPDATE workspaces SET \"Id\" = 2 WHERE \"Id\" = 1",
            "INSERT INTO workspaces (\"Id\", \"Revision\", \"Payload\") VALUES (2, 0, 'permission-probe')",
            "DROP TABLE audit_entries"
        ];
        foreach (var statement in forbiddenStatements)
        {
            await using var runtime = scope.CreateRuntimeContext(password);
            var error = await Assert.ThrowsAsync<PostgresException>(() => runtime.Database.ExecuteSqlRawAsync(statement));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        await using var reader = scope.CreateContext();
        Assert.Single(await reader.Audit.AsNoTracking().ToListAsync());
        Assert.Equal(1, (await reader.Workspaces.AsNoTracking().SingleAsync()).Revision);
    }

    [PostgresRoleFact]
    public async Task Provisioning_removes_preexisting_column_level_audit_update_grants()
    {
        await using var scope = await DatabaseScope.CreateAsync(DatabaseProvider.Postgres);
        var password = PasswordWithQuote();
        await using (var owner = scope.CreateContext())
        {
            await RuntimeRoleProvisioner.ProvisionAsync(owner, password);
            await owner.Database.ExecuteSqlRawAsync("GRANT UPDATE (\"Payload\") ON audit_entries TO teammy_runtime");
            Assert.True(await owner.Database.SqlQueryRaw<bool>(
                "SELECT has_column_privilege('teammy_runtime', 'audit_entries', 'Payload', 'UPDATE') AS \"Value\"").SingleAsync());
            await RuntimeRoleProvisioner.ProvisionAsync(owner, password);
            Assert.False(await owner.Database.SqlQueryRaw<bool>(
                "SELECT has_column_privilege('teammy_runtime', 'audit_entries', 'Payload', 'UPDATE') AS \"Value\"").SingleAsync());
        }

        await using var runtime = scope.CreateRuntimeContext(password);
        var error = await Assert.ThrowsAsync<PostgresException>(() => runtime.Database.ExecuteSqlRawAsync(
            "UPDATE audit_entries SET \"Payload\" = \"Payload\""));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [PostgresRoleFact]
    public async Task Inherited_public_schema_create_permission_is_rejected()
    {
        await using var scope = await DatabaseScope.CreateAsync(DatabaseProvider.Postgres);
        await using var owner = scope.CreateContext();
        // Schema is generated by DatabaseScope, never supplied by a caller.
        await using (var connection = new NpgsqlConnection(scope.ConnectionString))
        {
            await connection.OpenAsync();
            await using var grant = new NpgsqlCommand($"GRANT CREATE ON SCHEMA \"{scope.Schema}\" TO PUBLIC", connection);
            await grant.ExecuteNonQueryAsync();
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RuntimeRoleProvisioner.ProvisionAsync(owner, PasswordWithQuote()));
        Assert.Contains("CREATE", error.Message);
    }

    [PostgresRoleFact]
    public async Task Inherited_public_audit_column_update_permission_is_rejected()
    {
        await using var scope = await DatabaseScope.CreateAsync(DatabaseProvider.Postgres);
        await using var owner = scope.CreateContext();
        await owner.Database.ExecuteSqlRawAsync("GRANT UPDATE (\"Payload\") ON audit_entries TO PUBLIC");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RuntimeRoleProvisioner.ProvisionAsync(owner, PasswordWithQuote()));
        Assert.Contains("privilege", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string PasswordWithQuote() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "'quoted";
}
