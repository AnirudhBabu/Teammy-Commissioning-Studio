using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Teammy.Application;
using Teammy.Domain;
using Teammy.Infrastructure;

namespace Teammy.Infrastructure.Tests;

public sealed class SqliteRepositoryTests
{
    [Fact] public Task Real_migrations_match_the_model() => RepositoryContract.MigrationsMatchModel(DatabaseProvider.Sqlite);
    [Fact] public Task Bootstrap_is_repeatable_without_resetting_work() => RepositoryContract.BootstrapIsRepeatable(DatabaseProvider.Sqlite);
    [Fact] public Task Concurrent_contexts_do_not_lose_an_update() => RepositoryContract.ConcurrentWriters(DatabaseProvider.Sqlite);
    [Fact] public Task Audit_insert_failure_rolls_back_the_state_update() => RepositoryContract.AuditFailureRollsBack(DatabaseProvider.Sqlite);
    [Fact] public Task Audit_pagination_is_stable_and_bounded() => RepositoryContract.AuditPagination(DatabaseProvider.Sqlite);
    [Fact] public Task Mismatched_mutation_revisions_never_write() => RepositoryContract.InvalidMutationDoesNotWrite(DatabaseProvider.Sqlite);
    [Fact] public Task Uninitialized_database_fails_explicitly() => RepositoryContract.UninitializedRead(DatabaseProvider.Sqlite);
    [Fact] public Task Cancellation_does_not_write_partial_state() => RepositoryContract.CancelledWrite(DatabaseProvider.Sqlite);
}

public sealed class PostgresRepositoryTests
{
    [PostgresFact] public Task Real_migrations_match_the_model() => RepositoryContract.MigrationsMatchModel(DatabaseProvider.Postgres);
    [PostgresFact] public Task Bootstrap_is_repeatable_without_resetting_work() => RepositoryContract.BootstrapIsRepeatable(DatabaseProvider.Postgres);
    [PostgresFact] public Task Concurrent_contexts_do_not_lose_an_update() => RepositoryContract.ConcurrentWriters(DatabaseProvider.Postgres);
    [PostgresFact] public Task Audit_insert_failure_rolls_back_the_state_update() => RepositoryContract.AuditFailureRollsBack(DatabaseProvider.Postgres);
    [PostgresFact] public Task Audit_pagination_is_stable_and_bounded() => RepositoryContract.AuditPagination(DatabaseProvider.Postgres);
    [PostgresFact] public Task Mismatched_mutation_revisions_never_write() => RepositoryContract.InvalidMutationDoesNotWrite(DatabaseProvider.Postgres);
    [PostgresFact] public Task Uninitialized_database_fails_explicitly() => RepositoryContract.UninitializedRead(DatabaseProvider.Postgres);
    [PostgresFact] public Task Cancellation_does_not_write_partial_state() => RepositoryContract.CancelledWrite(DatabaseProvider.Postgres);
}

internal static class RepositoryContract
{
    public static async Task MigrationsMatchModel(DatabaseProvider provider)
    {
        await using var scope = await DatabaseScope.CreateAsync(provider);
        await using var context = scope.CreateContext();
        Assert.Single(await context.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Single(await context.Workspaces.AsNoTracking().ToListAsync());
        Assert.Empty(await context.Audit.AsNoTracking().ToListAsync());
        var state = await new WorkspaceRepository(context).ReadAsync();
        Assert.Equal(0, state.Revision);
        Assert.Equal(2, state.Projects.Length);
    }

    public static async Task BootstrapIsRepeatable(DatabaseProvider provider)
    {
        await using var scope = await DatabaseScope.CreateAsync(provider);
        await PersistWork(scope, "Preserve this engineering work");
        await using (var context = scope.CreateContext())
        {
            await DatabaseBootstrapper.InitializeAsync(context);
            await DatabaseBootstrapper.InitializeAsync(context);
        }

        await using var reader = scope.CreateContext();
        var repository = new WorkspaceRepository(reader);
        var state = await repository.ReadAsync();
        Assert.Equal(1, state.Revision);
        Assert.Contains(state.WorkItems, item => item.Title == "Preserve this engineering work");
        Assert.Single(await repository.ReadAuditAsync());
    }

    public static async Task ConcurrentWriters(DatabaseProvider provider)
    {
        await using var scope = await DatabaseScope.CreateAsync(provider);
        await using var firstContext = scope.CreateContext();
        await using var secondContext = scope.CreateContext();
        var first = new WorkspaceRepository(firstContext);
        var second = new WorkspaceRepository(secondContext);
        var firstSnapshot = await first.ReadAsync();
        var secondSnapshot = await second.ReadAsync();
        var firstMutation = Mutation(firstSnapshot, "Writer one change");
        var secondMutation = Mutation(secondSnapshot, "Writer two change");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Exception?> Write(WorkspaceRepository repository, WorkspaceMutation mutation)
        {
            await start.Task;
            try
            {
                await repository.SaveAsync(mutation, 0);
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        }

        var writers = new[] { Task.Run(() => Write(first, firstMutation)), Task.Run(() => Write(second, secondMutation)) };
        start.SetResult();
        var results = await Task.WhenAll(writers);
        Assert.Single(results, error => error is null);
        var conflict = Assert.IsType<WorkspaceException>(Assert.Single(results, error => error is not null));
        Assert.Equal(WorkspaceErrorKind.Conflict, conflict.Kind);
        await using var finalContext = scope.CreateContext();
        var finalRepository = new WorkspaceRepository(finalContext);
        var committed = await finalRepository.ReadAsync();
        Assert.Equal(1, committed.Revision);
        Assert.Equal(firstSnapshot.WorkItems.Length + 1, committed.WorkItems.Length);
        Assert.Single(committed.WorkItems.Where(item => item.Title.StartsWith("Writer ", StringComparison.Ordinal)));
        var audit = Assert.Single(await finalRepository.ReadAuditAsync());
        Assert.Equal(1, audit.Revision);
        Assert.Equal(committed.WorkItems[^1].Id, audit.ResourceId);
    }

    public static async Task AuditFailureRollsBack(DatabaseProvider provider)
    {
        await using var scope = await DatabaseScope.CreateAsync(provider);
        WorkspaceState original;
        await using (var context = scope.CreateContext())
        {
            original = await new WorkspaceRepository(context).ReadAsync();
            // Occupy the next audit key. The repository's UPDATE will run, then INSERT must fail.
            context.Audit.Add(new AuditRow
            {
                Revision = 1,
                Payload = JsonSerializer.Serialize(Mutation(original, "Sentinel").AuditEntry, WorkspaceRepository.JsonOptions)
            });
            await context.SaveChangesAsync();
        }

        await using (var writer = scope.CreateContext())
        {
            var repository = new WorkspaceRepository(writer);
            await Assert.ThrowsAsync<DbUpdateException>(() => repository.SaveAsync(Mutation(original, "Must roll back"), 0));
        }

        await using var reader = scope.CreateContext();
        var state = await new WorkspaceRepository(reader).ReadAsync();
        Assert.Equal(JsonSerializer.Serialize(original, WorkspaceRepository.JsonOptions), JsonSerializer.Serialize(state, WorkspaceRepository.JsonOptions));
        Assert.Equal(0, (await reader.Workspaces.AsNoTracking().SingleAsync()).Revision);
        Assert.Single(await reader.Audit.AsNoTracking().ToListAsync());
        Assert.DoesNotContain(state.WorkItems, item => item.Title == "Must roll back");
    }

    public static async Task AuditPagination(DatabaseProvider provider)
    {
        await using var scope = await DatabaseScope.CreateAsync(provider);
        for (var number = 1; number <= 5; number++)
        {
            await PersistWork(scope, $"Audited change {number}");
        }

        await using var context = scope.CreateContext();
        var repository = new WorkspaceRepository(context);
        var first = await repository.ReadAuditAsync(limit: 2);
        Assert.Equal([5, 4], first.Select(entry => entry.Revision));
        var second = await repository.ReadAuditAsync(limit: 2, beforeRevision: first[^1].Revision);
        Assert.Equal([3, 2], second.Select(entry => entry.Revision));
        var third = await repository.ReadAuditAsync(limit: 2, beforeRevision: second[^1].Revision);
        Assert.Equal(1, Assert.Single(third).Revision);
        Assert.Empty(await repository.ReadAuditAsync(limit: 2, beforeRevision: 1));
        Assert.Single(await repository.ReadAuditAsync(limit: 0));
        Assert.Equal(5, (await repository.ReadAuditAsync(limit: int.MaxValue)).Count);
    }

    public static async Task InvalidMutationDoesNotWrite(DatabaseProvider provider)
    {
        await using var scope = await DatabaseScope.CreateAsync(provider);
        await using var context = scope.CreateContext();
        var repository = new WorkspaceRepository(context);
        var state = await repository.ReadAsync();
        var valid = Mutation(state, "Invalid revision must fail");
        var invalid = valid with { AuditEntry = valid.AuditEntry with { Revision = 2 } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveAsync(invalid, state.Revision));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveAsync(valid, state.Revision + 1));
        Assert.Equal(0, (await repository.ReadAsync()).Revision);
        Assert.Empty(await repository.ReadAuditAsync());
    }

    public static async Task UninitializedRead(DatabaseProvider provider)
    {
        await using var scope = await DatabaseScope.CreateAsync(provider, initialize: false);
        await using var context = scope.CreateContext();
        await context.Database.MigrateAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkspaceRepository(context).ReadAsync());
        Assert.Contains("not initialized", error.Message);
    }

    public static async Task CancelledWrite(DatabaseProvider provider)
    {
        await using var scope = await DatabaseScope.CreateAsync(provider);
        await using var context = scope.CreateContext();
        var repository = new WorkspaceRepository(context);
        var state = await repository.ReadAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.SaveAsync(Mutation(state, "Cancelled change"), 0, cancellation.Token));
        await using var reader = scope.CreateContext();
        var readRepository = new WorkspaceRepository(reader);
        Assert.Equal(0, (await readRepository.ReadAsync()).Revision);
        Assert.Empty(await readRepository.ReadAuditAsync());
    }

    internal static async Task PersistWork(DatabaseScope scope, string title)
    {
        await using var context = scope.CreateContext();
        var repository = new WorkspaceRepository(context);
        var state = await repository.ReadAsync();
        await repository.SaveAsync(Mutation(state, title), state.Revision);
    }

    internal static WorkspaceMutation Mutation(WorkspaceState state, string title) => new WorkspaceEngine().Apply(state,
        new CreateWorkItemCommand(state.Revision, SeedWorkspace.ConveyorProjectId, title,
            "Persistence integration test.", SeedWorkspace.Engineer.Id, WorkItemPriority.Normal),
        SeedWorkspace.Engineer, DateTimeOffset.UtcNow);
}
