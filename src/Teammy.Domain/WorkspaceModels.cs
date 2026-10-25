using System.Collections.Immutable;

namespace Teammy.Domain;

public enum WorkspaceRole { Viewer, Engineer, Approver }
public enum WorkItemStatus { Backlog, InProgress, Blocked, Review, Done }
public enum WorkItemPriority { Low, Normal, High, Critical }
public enum ReleaseStatus { Pending, Approved, Rejected, Stale }
public enum WorkspaceErrorKind { Validation, Forbidden, NotFound, Conflict }

public sealed record Actor(string Id, string DisplayName, WorkspaceRole Role);
public sealed record Member(string Id, string DisplayName, WorkspaceRole Role);
public sealed record Team(string Id, string Name, ImmutableArray<string> MemberIds);
public sealed record EngineeringProject(string Id, string TeamId, string Name, string Description, string Location);

public sealed record WorkItem(
    string Id, string Code, string ProjectId, string Title, string Description,
    string AssigneeId, WorkItemPriority Priority, WorkItemStatus Status,
    DateOnly DueDate, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record DriveProfile(
    string Id, string Name, double MaxSpeedRpm, double RatedCurrentAmps,
    double MaxRampRateRpmPerSecond, double MaxTemperatureCelsius);

public sealed record DriveConfiguration(
    string Id, string ProjectId, string Name, string ProfileId,
    double TargetSpeedRpm, double AccelerationSeconds, double DecelerationSeconds,
    double CurrentLimitAmps, double LoadPercent, int Revision,
    string UpdatedBy, DateTimeOffset UpdatedAt);

public sealed record SimulationCheck(string Code, string Name, bool Passed, string Detail);
public sealed record TelemetrySample(double Seconds, double SpeedRpm, double CurrentAmps, double TemperatureCelsius);
public sealed record SimulationRun(
    string Id, string ConfigurationId, int ConfigurationRevision,
    string RequestedBy, DateTimeOffset CompletedAt, bool Passed,
    ImmutableArray<SimulationCheck> Checks, ImmutableArray<TelemetrySample> Samples);

public sealed record ReleaseCandidate(
    string Id, string ProjectId, ReleaseStatus Status, string Fingerprint,
    string RequestedBy, DateTimeOffset RequestedAt, string Notes,
    string? ReviewedBy, DateTimeOffset? ReviewedAt, string? ReviewNote,
    ImmutableArray<string> ConfigurationAuthors);

public sealed record AuditEntry(
    string Id, int Revision, DateTimeOffset Timestamp,
    string ActorId, string ActorName, WorkspaceRole ActorRole,
    string Action, string ResourceId, string Summary);

public sealed record WorkspaceState(
    int Revision,
    ImmutableArray<Team> Teams,
    ImmutableArray<Member> Members,
    ImmutableArray<EngineeringProject> Projects,
    ImmutableArray<WorkItem> WorkItems,
    ImmutableArray<DriveProfile> DriveProfiles,
    ImmutableArray<DriveConfiguration> Configurations,
    ImmutableArray<SimulationRun> SimulationRuns,
    ImmutableArray<ReleaseCandidate> Releases);

public sealed record ReadinessCheck(string Code, string Name, bool Passed, string Detail);
public sealed record ReleaseReadiness(bool IsReady, ImmutableArray<ReadinessCheck> Checks);

public sealed class WorkspaceException(WorkspaceErrorKind kind, string code, string message) : Exception(message)
{
    public WorkspaceErrorKind Kind { get; } = kind;
    public string Code { get; } = code;
}
