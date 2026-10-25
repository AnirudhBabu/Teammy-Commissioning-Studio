using Teammy.Application;
using Teammy.Domain;

namespace Teammy.Presentation.Clients;

/// <summary>Explicit, ephemeral simulator. It never contacts a controller, database, or API.</summary>
public sealed class DemoWorkspaceClient : IWorkspaceClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly WorkspaceEngine _engine = new();
    private readonly TimeProvider _clock;
    private readonly List<AuditEntry> _audit = [];
    private WorkspaceState _state;
    private Actor _actor;

    public DemoWorkspaceClient(WorkspaceState? initialState = null, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _state = initialState ?? SeedWorkspace.Create(_clock.GetUtcNow());
        _actor = SeedWorkspace.Engineer;
    }

    public string ModeLabel => "LOCAL DEMO";
    public string ConnectionDescription => "In-memory workspace · resets when closed · no hardware connection";
    public bool AllowsActorSwitch => true;

    public void SetDemoActor(Actor actor)
    {
        if (actor != SeedWorkspace.Engineer && actor != SeedWorkspace.Approver && actor != SeedWorkspace.Viewer)
        {
            throw new ArgumentException("Choose a supported demonstration identity.", nameof(actor));
        }

        _actor = actor;
    }

    public Task<Actor> GetActorAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_actor);
    }

    public async Task<WorkspaceState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _state;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AuditEntry>> GetAuditAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _audit.OrderByDescending(entry => entry.Revision).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkspaceState> ExecuteAsync(WorkspaceCommand command, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var actor = _actor;
            var mutation = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _engine.Apply(_state, command, actor, _clock.GetUtcNow());
            }, cancellationToken);
            // Once committed, return the new revision even if cancellation arrives now.
            _state = mutation.State;
            _audit.Add(mutation.AuditEntry);
            return _state;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
