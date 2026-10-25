using Teammy.Application;
using Teammy.Domain;

namespace Teammy.Presentation.Clients;

public interface IWorkspaceClient : IDisposable
{
    string ModeLabel { get; }
    string ConnectionDescription { get; }
    bool AllowsActorSwitch { get; }
    void SetDemoActor(Actor actor);
    Task<Actor> GetActorAsync(CancellationToken cancellationToken = default);
    Task<WorkspaceState> LoadAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AuditEntry>> GetAuditAsync(CancellationToken cancellationToken = default);
    Task<WorkspaceState> ExecuteAsync(WorkspaceCommand command, CancellationToken cancellationToken = default);
}
