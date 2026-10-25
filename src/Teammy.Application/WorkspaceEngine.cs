using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Teammy.Domain;

namespace Teammy.Application;

/// <summary>
/// The shared application boundary for the API and the explicit offline desktop sandbox.
/// Persistence must compare and swap Revision and append the returned audit in one transaction.
/// </summary>
public sealed class WorkspaceEngine(IDriveSimulator? simulator = null)
{
    private readonly IDriveSimulator _simulator = simulator ?? new DeterministicDriveSimulator();

    public WorkspaceMutation Apply(WorkspaceState state, WorkspaceCommand command, Actor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(actor);
        var member = state.Members.SingleOrDefault(item => item.Id == actor.Id);
        if (member is null || !Enum.IsDefined(actor.Role) || member.Role != actor.Role)
        {
            throw Error(WorkspaceErrorKind.Forbidden, "forbidden", "This identity has no matching workspace role.");
        }

        if (actor.Role == WorkspaceRole.Viewer)
        {
            throw Error(WorkspaceErrorKind.Forbidden, "forbidden", "Viewers cannot change the workspace.");
        }

        if (state.Revision != command.ExpectedRevision)
        {
            throw Error(WorkspaceErrorKind.Conflict, "revision_conflict", "The workspace changed. Refresh before trying again.");
        }

        var result = command switch
        {
            CreateWorkItemCommand create => CreateWorkItem(state, create, actor, now),
            TransitionWorkItemCommand transition => TransitionWorkItem(state, transition, actor, now),
            UpdateDriveConfigurationCommand configure => UpdateConfiguration(state, configure, actor, now),
            RunSimulationCommand simulate => RunSimulation(state, simulate, actor, now),
            RequestReleaseCommand request => RequestRelease(state, request, actor, now),
            ApproveReleaseCommand approve => ReviewRelease(state, approve.ReleaseId, actor, now, true, null),
            RejectReleaseCommand reject => ReviewRelease(state, reject.ReleaseId, actor, now, false, reject.Reason),
            _ => throw Error(WorkspaceErrorKind.Validation, "unsupported_command", "The requested operation is not supported.")
        };

        var revision = checked(state.Revision + 1);
        return new WorkspaceMutation(
            result.State with { Revision = revision },
            new AuditEntry($"audit-{revision:D8}", revision, now, member.Id, member.DisplayName, member.Role,
                result.Action, result.ResourceId, result.Summary));
    }

    public static ReleaseReadiness EvaluateReleaseReadiness(WorkspaceState state, string projectId)
    {
        _ = FindProject(state, projectId);
        var configurations = state.Configurations.Where(configuration => configuration.ProjectId == projectId).ToArray();
        var blockingItems = state.WorkItems.Where(item => item.ProjectId == projectId &&
            item.Priority is WorkItemPriority.High or WorkItemPriority.Critical && item.Status != WorkItemStatus.Done).ToArray();
        var missingEvidence = configurations.Where(configuration =>
        {
            var latest = state.SimulationRuns.LastOrDefault(run => run.ConfigurationId == configuration.Id && run.ConfigurationRevision == configuration.Revision);
            return latest is null || !latest.Passed;
        }).ToArray();

        var checks = ImmutableArray.Create(
            new ReadinessCheck("blocking-work", "High and critical work complete", blockingItems.Length == 0,
                blockingItems.Length == 0 ? "All high and critical work items are done." : $"{blockingItems.Length} high or critical work item(s) remain open."),
            new ReadinessCheck("drive-configurations", "Drive configuration available", configurations.Length > 0,
                configurations.Length > 0 ? $"{configurations.Length} drive configuration(s) in scope." : "Add a drive configuration before requesting a release."),
            new ReadinessCheck("current-evidence", "Current simulation evidence passes", configurations.Length > 0 && missingEvidence.Length == 0,
                missingEvidence.Length == 0 && configurations.Length > 0
                    ? "Every current configuration revision has passing simulation evidence."
                    : $"{missingEvidence.Length} configuration(s) need a passing simulation of their current revision."));
        return new ReleaseReadiness(checks.All(check => check.Passed), checks);
    }

    public static ImmutableArray<WorkItemStatus> AllowedTransitions(WorkItemStatus status) => status switch
    {
        WorkItemStatus.Backlog => [WorkItemStatus.InProgress],
        WorkItemStatus.InProgress => [WorkItemStatus.Blocked, WorkItemStatus.Review],
        WorkItemStatus.Blocked => [WorkItemStatus.InProgress],
        WorkItemStatus.Review => [WorkItemStatus.InProgress, WorkItemStatus.Done],
        WorkItemStatus.Done => [WorkItemStatus.InProgress],
        _ => []
    };

    private static OperationResult CreateWorkItem(WorkspaceState state, CreateWorkItemCommand command, Actor actor, DateTimeOffset now)
    {
        RequireRole(actor, WorkspaceRole.Engineer);
        var project = FindProject(state, command.ProjectId);
        RequireMembership(state, project, actor.Id);
        RequireMembership(state, project, command.AssigneeId);
        RequireCapacity(state.WorkItems.Length, 1000, "work items");
        var title = Text(command.Title, "Title", 3, 140);
        var description = Text(command.Description, "Description", 0, 2000);
        if (!Enum.IsDefined(command.Priority))
        {
            throw Error(WorkspaceErrorKind.Validation, "validation_failed", "Choose a supported work priority.");
        }

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var dueDate = command.DueDate ?? today.AddDays(7);
        if (dueDate < today || dueDate > today.AddYears(2))
        {
            throw Error(WorkspaceErrorKind.Validation, "validation_failed", "Due date must be between today and two years from today.");
        }

        var sequence = state.WorkItems.Length + 1;
        var item = new WorkItem($"work-{sequence:D4}", $"TC-{sequence:D3}", project.Id, title, description,
            command.AssigneeId, command.Priority, WorkItemStatus.Backlog, dueDate, now, now);
        var updated = InvalidateCandidates(state with { WorkItems = state.WorkItems.Add(item) }, project.Id);
        return new(updated, "work.created", item.Id, $"Created {item.Code}: {item.Title}");
    }

    private static OperationResult TransitionWorkItem(WorkspaceState state, TransitionWorkItemCommand command, Actor actor, DateTimeOffset now)
    {
        RequireRole(actor, WorkspaceRole.Engineer);
        var item = state.WorkItems.SingleOrDefault(item => item.Id == command.WorkItemId)
            ?? throw Error(WorkspaceErrorKind.NotFound, "not_found", "The work item does not exist.");
        RequireMembership(state, FindProject(state, item.ProjectId), actor.Id);
        if (!AllowedTransitions(item.Status).Contains(command.Status))
        {
            throw Error(WorkspaceErrorKind.Conflict, "invalid_transition", $"Work cannot move directly from {item.Status} to {command.Status}.");
        }

        var replacement = item with { Status = command.Status, UpdatedAt = now };
        var updated = InvalidateCandidates(state with { WorkItems = state.WorkItems.Replace(item, replacement) }, item.ProjectId);
        return new(updated, "work.transitioned", item.Id, $"Moved {item.Code} from {item.Status} to {command.Status}.");
    }

    private static OperationResult UpdateConfiguration(WorkspaceState state, UpdateDriveConfigurationCommand command, Actor actor, DateTimeOffset now)
    {
        RequireRole(actor, WorkspaceRole.Engineer);
        var configuration = FindConfiguration(state, command.ConfigurationId);
        RequireMembership(state, FindProject(state, configuration.ProjectId), actor.Id);
        var updatedConfiguration = configuration with
        {
            TargetSpeedRpm = command.TargetSpeedRpm,
            AccelerationSeconds = command.AccelerationSeconds,
            DecelerationSeconds = command.DecelerationSeconds,
            CurrentLimitAmps = command.CurrentLimitAmps,
            LoadPercent = command.LoadPercent,
            Revision = checked(configuration.Revision + 1),
            UpdatedBy = actor.Id,
            UpdatedAt = now
        };
        ConfigurationRules.Validate(updatedConfiguration, FindProfile(state, configuration.ProfileId));
        var updated = InvalidateCandidates(state with { Configurations = state.Configurations.Replace(configuration, updatedConfiguration) }, configuration.ProjectId);
        return new(updated, "configuration.updated", configuration.Id,
            $"Saved {configuration.Name} revision {updatedConfiguration.Revision}; new simulation evidence is required.");
    }

    private OperationResult RunSimulation(WorkspaceState state, RunSimulationCommand command, Actor actor, DateTimeOffset now)
    {
        RequireRole(actor, WorkspaceRole.Engineer);
        var configuration = FindConfiguration(state, command.ConfigurationId);
        RequireMembership(state, FindProject(state, configuration.ProjectId), actor.Id);
        RequireCapacity(state.SimulationRuns.Length, 2000, "simulation runs");
        var runId = $"simulation-{state.Revision + 1:D6}";
        var run = _simulator.Simulate(configuration, FindProfile(state, configuration.ProfileId), runId, actor, now);
        if (run.Id != runId || run.ConfigurationId != configuration.Id || run.ConfigurationRevision != configuration.Revision ||
            run.RequestedBy != actor.Id || run.CompletedAt != now ||
            run.Checks.IsDefaultOrEmpty || run.Passed != run.Checks.All(check => check.Passed) ||
            run.Samples.IsDefaultOrEmpty || run.Samples.Length > 120 || run.Samples.Any(sample =>
                !double.IsFinite(sample.Seconds) || !double.IsFinite(sample.SpeedRpm) ||
                !double.IsFinite(sample.CurrentAmps) || !double.IsFinite(sample.TemperatureCelsius)))
        {
            throw Error(WorkspaceErrorKind.Validation, "invalid_simulation_evidence", "The simulator returned inconsistent evidence.");
        }

        var updated = InvalidateCandidates(state with { SimulationRuns = state.SimulationRuns.Add(run) }, configuration.ProjectId);
        return new(updated, "simulation.completed", configuration.Id,
            $"Simulated {configuration.Name} revision {configuration.Revision}: {(run.Passed ? "passed" : "failed")}.");
    }

    private static OperationResult RequestRelease(WorkspaceState state, RequestReleaseCommand command, Actor actor, DateTimeOffset now)
    {
        RequireRole(actor, WorkspaceRole.Engineer);
        var project = FindProject(state, command.ProjectId);
        RequireMembership(state, project, actor.Id);
        RequireCapacity(state.Releases.Length, 1000, "release candidates");
        var notes = Text(command.Notes, "Release notes", 5, 2000);
        if (state.Releases.Any(release => release.ProjectId == project.Id && release.Status == ReleaseStatus.Pending))
        {
            throw Error(WorkspaceErrorKind.Conflict, "release_already_pending", "This project already has a pending release candidate.");
        }

        var readiness = EvaluateReleaseReadiness(state, project.Id);
        if (!readiness.IsReady)
        {
            throw Error(WorkspaceErrorKind.Conflict, "release_not_ready", string.Join(" ", readiness.Checks.Where(check => !check.Passed).Select(check => check.Detail)));
        }

        var candidate = new ReleaseCandidate($"release-{state.Revision + 1:D6}", project.Id, ReleaseStatus.Pending,
            Fingerprint(state, project.Id), actor.Id, now, notes, null, null, null,
            state.Configurations.Where(configuration => configuration.ProjectId == project.Id)
                .Select(configuration => configuration.UpdatedBy).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray());
        return new(state with { Releases = state.Releases.Add(candidate) }, "release.requested", candidate.Id,
            $"Requested independent review of {project.Name}.");
    }

    private static OperationResult ReviewRelease(WorkspaceState state, string releaseId, Actor actor, DateTimeOffset now, bool approve, string? reason)
    {
        RequireRole(actor, WorkspaceRole.Approver);
        var release = state.Releases.SingleOrDefault(candidate => candidate.Id == releaseId)
            ?? throw Error(WorkspaceErrorKind.NotFound, "not_found", "The release candidate does not exist.");
        RequireMembership(state, FindProject(state, release.ProjectId), actor.Id);
        if (release.Status == ReleaseStatus.Stale)
        {
            throw Error(WorkspaceErrorKind.Conflict, "release_stale", "The project changed after submission. Request a new release candidate.");
        }

        if (release.Status != ReleaseStatus.Pending)
        {
            throw Error(WorkspaceErrorKind.Conflict, "release_already_reviewed", "Only pending release candidates can be reviewed.");
        }

        if (release.RequestedBy == actor.Id || release.ConfigurationAuthors.Contains(actor.Id, StringComparer.Ordinal))
        {
            throw Error(WorkspaceErrorKind.Forbidden, "self_approval_forbidden", "The reviewer must be independent of the requestor and configuration authors.");
        }

        if (release.Fingerprint != Fingerprint(state, release.ProjectId) || !EvaluateReleaseReadiness(state, release.ProjectId).IsReady)
        {
            throw Error(WorkspaceErrorKind.Conflict, "release_stale", "The candidate no longer matches current project evidence. Request a new candidate.");
        }

        var note = approve ? "Independent review approved. Simulation evidence only; no hardware deployment is performed."
            : Text(reason, "Rejection reason", 5, 2000);
        var reviewed = release with
        {
            Status = approve ? ReleaseStatus.Approved : ReleaseStatus.Rejected,
            ReviewedBy = actor.Id,
            ReviewedAt = now,
            ReviewNote = note
        };
        return new(state with { Releases = state.Releases.Replace(release, reviewed) },
            approve ? "release.approved" : "release.rejected", release.Id,
            approve ? "Approved the independently reviewed simulation release candidate." : $"Rejected release candidate: {note}");
    }

    private static WorkspaceState InvalidateCandidates(WorkspaceState state, string projectId) => state with
    {
        Releases = state.Releases.Select(release => release.ProjectId == projectId && release.Status == ReleaseStatus.Pending
            ? release with { Status = ReleaseStatus.Stale, ReviewNote = "Project work, configuration, or evidence changed after submission." }
            : release).ToImmutableArray()
    };

    private static string Fingerprint(WorkspaceState state, string projectId)
    {
        var configurations = state.Configurations.Where(item => item.ProjectId == projectId).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var snapshot = new
        {
            ProjectId = projectId,
            WorkItems = state.WorkItems.Where(item => item.ProjectId == projectId).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(),
            Configurations = configurations,
            Evidence = configurations.Select(configuration => state.SimulationRuns.LastOrDefault(run =>
                run.ConfigurationId == configuration.Id && run.ConfigurationRevision == configuration.Revision)).ToArray()
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot)));
    }

    private static EngineeringProject FindProject(WorkspaceState state, string id) =>
        state.Projects.SingleOrDefault(project => project.Id == id)
        ?? throw Error(WorkspaceErrorKind.NotFound, "not_found", "The project does not exist.");

    private static DriveConfiguration FindConfiguration(WorkspaceState state, string id) =>
        state.Configurations.SingleOrDefault(configuration => configuration.Id == id)
        ?? throw Error(WorkspaceErrorKind.NotFound, "not_found", "The drive configuration does not exist.");

    private static DriveProfile FindProfile(WorkspaceState state, string id) =>
        state.DriveProfiles.SingleOrDefault(profile => profile.Id == id)
        ?? throw Error(WorkspaceErrorKind.NotFound, "not_found", "The drive profile does not exist.");

    private static void RequireMembership(WorkspaceState state, EngineeringProject project, string memberId)
    {
        var team = state.Teams.SingleOrDefault(team => team.Id == project.TeamId);
        if (team is null || !team.MemberIds.Contains(memberId, StringComparer.Ordinal) || !state.Members.Any(member => member.Id == memberId))
        {
            throw Error(WorkspaceErrorKind.Forbidden, "project_access_denied", "The selected identity must belong to this project's team.");
        }
    }

    private static void RequireRole(Actor actor, WorkspaceRole role)
    {
        if (actor.Role != role)
        {
            throw Error(WorkspaceErrorKind.Forbidden, "forbidden", $"This operation requires the {role} role.");
        }
    }

    private static string Text(string? value, string name, int minimum, int maximum)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length < minimum || trimmed.Length > maximum || trimmed.Any(character => char.IsControl(character) && character is not '\n' and not '\r' and not '\t'))
        {
            throw Error(WorkspaceErrorKind.Validation, "validation_failed", $"{name} must contain {minimum}-{maximum} characters and no unsupported control characters.");
        }

        return trimmed;
    }

    private static void RequireCapacity(int current, int maximum, string resource)
    {
        if (current >= maximum)
        {
            throw Error(WorkspaceErrorKind.Conflict, "workspace_capacity_reached", $"This demonstration workspace supports at most {maximum} {resource}. Archive this lab before creating more.");
        }
    }

    private static WorkspaceException Error(WorkspaceErrorKind kind, string code, string message) => new(kind, code, message);
    private sealed record OperationResult(WorkspaceState State, string Action, string ResourceId, string Summary);
}
