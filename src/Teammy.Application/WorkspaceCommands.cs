using System.Text.Json.Serialization;
using Teammy.Domain;

namespace Teammy.Application;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CreateWorkItemCommand), "createWorkItem")]
[JsonDerivedType(typeof(TransitionWorkItemCommand), "transitionWorkItem")]
[JsonDerivedType(typeof(UpdateDriveConfigurationCommand), "updateDriveConfiguration")]
[JsonDerivedType(typeof(RunSimulationCommand), "runSimulation")]
[JsonDerivedType(typeof(RequestReleaseCommand), "requestRelease")]
[JsonDerivedType(typeof(ApproveReleaseCommand), "approveRelease")]
[JsonDerivedType(typeof(RejectReleaseCommand), "rejectRelease")]
public abstract record WorkspaceCommand(int ExpectedRevision);

public sealed record CreateWorkItemCommand(
    int ExpectedRevision, string ProjectId, string Title, string Description,
    string AssigneeId, WorkItemPriority Priority, DateOnly? DueDate = null) : WorkspaceCommand(ExpectedRevision);
public sealed record TransitionWorkItemCommand(
    int ExpectedRevision, string WorkItemId, WorkItemStatus Status) : WorkspaceCommand(ExpectedRevision);
public sealed record UpdateDriveConfigurationCommand(
    int ExpectedRevision, string ConfigurationId, double TargetSpeedRpm,
    double AccelerationSeconds, double DecelerationSeconds,
    double CurrentLimitAmps, double LoadPercent) : WorkspaceCommand(ExpectedRevision);
public sealed record RunSimulationCommand(
    int ExpectedRevision, string ConfigurationId) : WorkspaceCommand(ExpectedRevision);
public sealed record RequestReleaseCommand(
    int ExpectedRevision, string ProjectId, string Notes) : WorkspaceCommand(ExpectedRevision);
public sealed record ApproveReleaseCommand(
    int ExpectedRevision, string ReleaseId) : WorkspaceCommand(ExpectedRevision);
public sealed record RejectReleaseCommand(
    int ExpectedRevision, string ReleaseId, string Reason) : WorkspaceCommand(ExpectedRevision);

public sealed record WorkspaceMutation(WorkspaceState State, AuditEntry AuditEntry);
