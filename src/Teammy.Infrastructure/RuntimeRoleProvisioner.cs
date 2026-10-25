using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Teammy.Infrastructure;

/// <summary>Optional migration-only provisioning of the restricted PostgreSQL application login.</summary>
public static class RuntimeRoleProvisioner
{
    public const string RoleName = "teammy_runtime";

    public static async Task ProvisionAsync(WorkspaceDbContext database, string password,
        CancellationToken cancellationToken = default)
    {
        if (!database.Database.IsNpgsql())
        {
            throw new InvalidOperationException("A runtime database role applies only to PostgreSQL.");
        }

        if (password.Length < 32 || password.Any(char.IsControl))
        {
            throw new InvalidOperationException("The runtime password must have at least 32 characters and no control characters.");
        }

        await database.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = (NpgsqlConnection)database.Database.GetDbConnection();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var inspect = new NpgsqlCommand("""
                SELECT CASE WHEN rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls
                    OR EXISTS(SELECT 1 FROM pg_auth_members WHERE member = pg_roles.oid)
                    THEN 'unsafe' ELSE 'safe' END
                FROM pg_roles WHERE rolname = 'teammy_runtime'
                """, connection, transaction);
            var existing = (string?)await inspect.ExecuteScalarAsync(cancellationToken);
            if (existing == "unsafe")
            {
                throw new InvalidOperationException("The existing runtime role has unexpected elevated privileges or memberships.");
            }

            // PostgreSQL quotes both identifiers and the password. Never concatenate user input into SQL.
            // Npgsql commands are intentionally not attached to verbose SQL/parameter logging.
            await using var format = new NpgsqlCommand(existing is null
                ? "SELECT format('CREATE ROLE %I LOGIN NOINHERIT PASSWORD %L', @role, @password)"
                : "SELECT format('ALTER ROLE %I LOGIN NOINHERIT PASSWORD %L', @role, @password)", connection, transaction);
            format.Parameters.AddWithValue("role", RoleName);
            format.Parameters.AddWithValue("password", password);
            var roleSql = (string)(await format.ExecuteScalarAsync(cancellationToken))!;
            await using (var create = new NpgsqlCommand(roleSql, connection, transaction))
            {
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var grants = new NpgsqlCommand("""
                SELECT format(
                    'GRANT CONNECT ON DATABASE %I TO teammy_runtime;
                     REVOKE ALL ON SCHEMA %I FROM teammy_runtime;
                     GRANT USAGE ON SCHEMA %I TO teammy_runtime;
                     REVOKE ALL ON TABLE %I.workspaces, %I.audit_entries FROM teammy_runtime;
                     REVOKE SELECT ("Id", "Revision", "Payload"), INSERT ("Id", "Revision", "Payload"),
                        UPDATE ("Id", "Revision", "Payload"), REFERENCES ("Id", "Revision", "Payload")
                        ON TABLE %I.workspaces FROM teammy_runtime;
                     REVOKE SELECT ("Revision", "Payload"), INSERT ("Revision", "Payload"),
                        UPDATE ("Revision", "Payload"), REFERENCES ("Revision", "Payload")
                        ON TABLE %I.audit_entries FROM teammy_runtime;
                     GRANT SELECT ON TABLE %I.workspaces, %I.audit_entries TO teammy_runtime;
                     GRANT UPDATE ("Revision", "Payload") ON TABLE %I.workspaces TO teammy_runtime;
                     GRANT INSERT ("Revision", "Payload") ON TABLE %I.audit_entries TO teammy_runtime;'
                     , current_database(), current_schema(), current_schema(), current_schema(), current_schema(),
                       current_schema(), current_schema(), current_schema(), current_schema(),
                       current_schema(), current_schema())
                """, connection, transaction);
            var grantsSql = (string)(await grants.ExecuteScalarAsync(cancellationToken))!;
            await using (var grant = new NpgsqlCommand(grantsSql, connection, transaction))
            {
                await grant.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var inspectSchema = new NpgsqlCommand(
                "SELECT has_schema_privilege('teammy_runtime', current_schema(), 'CREATE')", connection, transaction);
            if ((bool)(await inspectSchema.ExecuteScalarAsync(cancellationToken))!)
            {
                throw new InvalidOperationException("The runtime role inherits schema CREATE permission. Use a dedicated schema without PUBLIC CREATE permission.");
            }

            await using var inspectTables = new NpgsqlCommand("""
                SELECT has_table_privilege('teammy_runtime', 'workspaces', 'INSERT,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                    OR has_column_privilege('teammy_runtime', 'workspaces', 'Id', 'INSERT,UPDATE,REFERENCES')
                    OR has_column_privilege('teammy_runtime', 'workspaces', 'Revision', 'INSERT,REFERENCES')
                    OR has_column_privilege('teammy_runtime', 'workspaces', 'Payload', 'INSERT,REFERENCES')
                    OR has_table_privilege('teammy_runtime', 'audit_entries', 'UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                    OR has_column_privilege('teammy_runtime', 'audit_entries', 'Revision', 'UPDATE,REFERENCES')
                    OR has_column_privilege('teammy_runtime', 'audit_entries', 'Payload', 'UPDATE,REFERENCES')
                """, connection, transaction);
            if ((bool)(await inspectTables.ExecuteScalarAsync(cancellationToken))!)
            {
                throw new InvalidOperationException("The runtime role retains unexpected table privileges, ownership, or PUBLIC grants.");
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            await database.Database.CloseConnectionAsync();
        }
    }
}
