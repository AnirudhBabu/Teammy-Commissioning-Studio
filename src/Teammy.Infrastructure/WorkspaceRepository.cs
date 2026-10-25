using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Teammy.Application;
using Teammy.Domain;

namespace Teammy.Infrastructure;

public interface IWorkspaceRepository
{
    Task<WorkspaceState> ReadAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AuditEntry>> ReadAuditAsync(int limit = 100, int beforeRevision = int.MaxValue,
        CancellationToken cancellationToken = default);
    Task SaveAsync(WorkspaceMutation mutation, int expectedRevision, CancellationToken cancellationToken = default);
}

public sealed class WorkspaceRepository(WorkspaceDbContext database) : IWorkspaceRepository
{
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    public async Task<WorkspaceState> ReadAsync(CancellationToken cancellationToken = default)
    {
        var row = await database.Workspaces.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == 1, cancellationToken)
            ?? throw new InvalidOperationException("Workspace is not initialized. Run the migration command first.");
        return JsonSerializer.Deserialize<WorkspaceState>(row.Payload, JsonOptions)
            ?? throw new InvalidOperationException("Workspace data is not readable.");
    }

    public async Task<IReadOnlyList<AuditEntry>> ReadAuditAsync(int limit = 100, int beforeRevision = int.MaxValue,
        CancellationToken cancellationToken = default)
    {
        var rows = await database.Audit.AsNoTracking()
            .Where(row => row.Revision < beforeRevision)
            .OrderByDescending(row => row.Revision)
            .Take(Math.Clamp(limit, 1, 500)).ToListAsync(cancellationToken);
        return rows.Select(row => JsonSerializer.Deserialize<AuditEntry>(row.Payload, JsonOptions)
            ?? throw new InvalidOperationException("Audit entry is not readable.")).ToArray();
    }

    public async Task SaveAsync(WorkspaceMutation mutation, int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (mutation.State.Revision != expectedRevision + 1 ||
            mutation.AuditEntry.Revision != mutation.State.Revision)
        {
            throw new InvalidOperationException("State and audit revisions must advance together.");
        }

        // One database transaction, one compare-and-swap, one immutable audit insertion.
        // This works across processes/replicas; an in-memory lock would not.
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var payload = JsonSerializer.Serialize(mutation.State, JsonOptions);
        var changed = await database.Workspaces
            .Where(row => row.Id == 1 && row.Revision == expectedRevision)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Revision, mutation.State.Revision)
                .SetProperty(row => row.Payload, payload), cancellationToken);
        if (changed != 1)
        {
            throw new WorkspaceException(WorkspaceErrorKind.Conflict, "workspace.conflict",
                "Another engineer changed the workspace. Refresh and retry your action.");
        }

        database.Audit.Add(new AuditRow
        {
            Revision = mutation.AuditEntry.Revision,
            Payload = JsonSerializer.Serialize(mutation.AuditEntry, JsonOptions)
        });
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
