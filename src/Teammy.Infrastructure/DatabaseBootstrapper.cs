using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Teammy.Application;

namespace Teammy.Infrastructure;

public static class DatabaseBootstrapper
{
    public static async Task InitializeAsync(WorkspaceDbContext database, CancellationToken cancellationToken = default)
    {
        await database.Database.MigrateAsync(cancellationToken);
        if (await database.Workspaces.AnyAsync(cancellationToken))
        {
            return;
        }

        var state = SeedWorkspace.Create(DateTimeOffset.UtcNow);
        database.Workspaces.Add(new WorkspaceRow
        {
            Id = 1,
            Revision = state.Revision,
            Payload = JsonSerializer.Serialize(state, WorkspaceRepository.JsonOptions)
        });
        await database.SaveChangesAsync(cancellationToken);
    }
}
