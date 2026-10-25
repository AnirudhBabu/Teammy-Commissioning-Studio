using Microsoft.EntityFrameworkCore;
using Npgsql;
using Teammy.Infrastructure;

namespace Teammy.Infrastructure.Tests;

internal enum DatabaseProvider { Sqlite, Postgres }

/// <summary>
/// A temporary SQLite file or a uniquely named PostgreSQL schema. Never creates,
/// deletes, or migrates tables in the caller's existing schema or database.
/// </summary>
internal sealed class DatabaseScope : IAsyncDisposable
{
    private readonly string? _directory;
    private readonly string? _administrativeConnection;
    public DatabaseProvider Provider { get; }
    public string ConnectionString { get; }
    public string? Schema { get; }

    private DatabaseScope(DatabaseProvider provider, string connectionString, string? directory, string? schema, string? administrativeConnection)
    {
        Provider = provider;
        ConnectionString = connectionString;
        _directory = directory;
        Schema = schema;
        _administrativeConnection = administrativeConnection;
    }

    public static async Task<DatabaseScope> CreateAsync(DatabaseProvider provider, bool initialize = true)
    {
        DatabaseScope scope;
        if (provider == DatabaseProvider.Sqlite)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"teammy-db-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            scope = new DatabaseScope(provider,
                $"Data Source={Path.Combine(directory, "workspace.db")};Default Timeout=30;Pooling=False", directory, null, null);
        }
        else
        {
            var supplied = Environment.GetEnvironmentVariable("TEAMMY_TEST_POSTGRES")
                ?? throw new InvalidOperationException("TEAMMY_TEST_POSTGRES must identify a PostgreSQL test database.");
            var schema = $"teammy_test_{Guid.NewGuid():N}";
            var administrative = new NpgsqlConnectionStringBuilder(supplied) { Pooling = false }.ConnectionString;
            await using (var connection = new NpgsqlConnection(administrative))
            {
                await connection.OpenAsync();
                // The identifier contains only our fixed prefix and generated lowercase hex characters.
                await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection);
                await command.ExecuteNonQueryAsync();
            }

            var scoped = new NpgsqlConnectionStringBuilder(supplied) { SearchPath = schema, Pooling = false }.ConnectionString;
            scope = new DatabaseScope(provider, scoped, null, schema, administrative);
        }

        try
        {
            if (initialize)
            {
                await using var context = scope.CreateContext();
                await DatabaseBootstrapper.InitializeAsync(context);
            }

            return scope;
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    public WorkspaceDbContext CreateContext() => Provider == DatabaseProvider.Sqlite
        ? new SqliteWorkspaceDbContext(new DbContextOptionsBuilder<SqliteWorkspaceDbContext>().UseSqlite(ConnectionString).Options)
        : new PostgresWorkspaceDbContext(new DbContextOptionsBuilder<PostgresWorkspaceDbContext>().UseNpgsql(ConnectionString).Options);

    public PostgresWorkspaceDbContext CreateRuntimeContext(string password)
    {
        var connection = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Username = RuntimeRoleProvisioner.RoleName,
            Password = password,
            Pooling = false
        };
        return new PostgresWorkspaceDbContext(new DbContextOptionsBuilder<PostgresWorkspaceDbContext>().UseNpgsql(connection.ConnectionString).Options);
    }

    public async ValueTask DisposeAsync()
    {
        if (Schema is not null && _administrativeConnection is not null)
        {
            await using var connection = new NpgsqlConnection(_administrativeConnection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }

        if (_directory is not null && Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEAMMY_TEST_POSTGRES")))
        {
            Skip = "Set TEAMMY_TEST_POSTGRES to run this test against an actual PostgreSQL server; no in-memory substitute is used.";
        }
    }
}

public sealed class PostgresRoleFactAttribute : FactAttribute
{
    public PostgresRoleFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEAMMY_TEST_POSTGRES")) ||
            Environment.GetEnvironmentVariable("TEAMMY_TEST_PROVISION_ROLE") != "1")
        {
            Skip = "Set TEAMMY_TEST_POSTGRES and TEAMMY_TEST_PROVISION_ROLE=1 on a disposable test server to verify the fixed runtime role.";
        }
    }
}
