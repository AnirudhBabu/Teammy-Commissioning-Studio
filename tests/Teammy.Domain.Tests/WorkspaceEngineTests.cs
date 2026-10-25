using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Teammy.Application;
using Teammy.Domain;

namespace Teammy.Domain.Tests;

public sealed class WorkspaceEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);
    private readonly WorkspaceEngine _engine = new();
    private static WorkspaceState Seed() => SeedWorkspace.Create(Now);

    [Fact]
    public void Successful_mutation_returns_new_state_and_trusted_audit_together()
    {
        var original = Seed();
        var mutation = _engine.Apply(original, Create(original), SeedWorkspace.Engineer with { DisplayName = "Untrusted alias" }, Now);

        Assert.Equal(0, original.Revision);
        Assert.Equal(8, original.WorkItems.Length);
        Assert.Equal(1, mutation.State.Revision);
        Assert.Equal(9, mutation.State.WorkItems.Length);
        Assert.Equal(mutation.State.Revision, mutation.AuditEntry.Revision);
        Assert.Equal(SeedWorkspace.Engineer.DisplayName, mutation.AuditEntry.ActorName);
        Assert.Equal(SeedWorkspace.Engineer.Id, mutation.AuditEntry.ActorId);
        Assert.Equal(Now, mutation.AuditEntry.Timestamp);
        Assert.Equal("work.created", mutation.AuditEntry.Action);
        Assert.Equal(mutation.State.WorkItems[^1].Id, mutation.AuditEntry.ResourceId);
    }

    [Fact]
    public void Concurrent_edit_requires_refresh_instead_of_overwriting()
    {
        var state = Seed();
        var command = Create(state);
        var newer = _engine.Apply(state, command, SeedWorkspace.Engineer, Now).State;

        AssertError("revision_conflict", () => _engine.Apply(newer, command, SeedWorkspace.Engineer, Now));
        Assert.Equal(9, newer.WorkItems.Length);
    }

    [Theory]
    [InlineData(WorkspaceRole.Engineer)]
    [InlineData(WorkspaceRole.Approver)]
    [InlineData(WorkspaceRole.Viewer)]
    public void Unregistered_identities_cannot_mutate(WorkspaceRole role)
    {
        var state = Seed();
        AssertError("forbidden", () => _engine.Apply(state, Create(state), new Actor("unknown", "Unknown", role), Now));
    }

    [Fact]
    public void Caller_cannot_promote_a_workspace_viewer_by_changing_role()
    {
        var state = Seed();
        AssertError("forbidden", () => _engine.Apply(state, Create(state), SeedWorkspace.Viewer with { Role = WorkspaceRole.Engineer }, Now));
    }

    [Fact]
    public void Undefined_actor_roles_fail_closed()
    {
        var state = Seed();
        AssertError("forbidden", () => _engine.Apply(state, Create(state), SeedWorkspace.Engineer with { Role = (WorkspaceRole)999 }, Now));
    }

    public static IEnumerable<object[]> WriteCommands()
    {
        yield return [new CreateWorkItemCommand(0, SeedWorkspace.ConveyorProjectId, "Review the ramp", "", SeedWorkspace.Engineer.Id, WorkItemPriority.Normal)];
        yield return [new TransitionWorkItemCommand(0, "work-0001", WorkItemStatus.Review)];
        yield return [new UpdateDriveConfigurationCommand(0, SeedWorkspace.ConveyorDriveId, 1450, 4, 5, 11.5, 55)];
        yield return [new RunSimulationCommand(0, SeedWorkspace.ConveyorDriveId)];
        yield return [new RequestReleaseCommand(0, SeedWorkspace.PumpProjectId, "Ready for review")];
        yield return [new ApproveReleaseCommand(0, "release-missing")];
        yield return [new RejectReleaseCommand(0, "release-missing", "Requires more evidence")];
    }

    [Theory]
    [MemberData(nameof(WriteCommands))]
    public void Viewer_cannot_use_any_write_operation(WorkspaceCommand command) =>
        AssertError("forbidden", () => _engine.Apply(Seed(), command, SeedWorkspace.Viewer, Now));

    [Theory]
    [MemberData(nameof(EngineeringCommands))]
    public void Approver_cannot_edit_engineering_evidence(WorkspaceCommand command) =>
        AssertError("forbidden", () => _engine.Apply(Seed(), command, SeedWorkspace.Approver, Now));

    public static IEnumerable<object[]> EngineeringCommands() => WriteCommands().Take(5);

    [Fact]
    public void Project_membership_is_enforced_for_the_actor()
    {
        var state = Seed();
        var outsider = new Actor("engineer-mateo", "Mateo Ruiz", WorkspaceRole.Engineer);
        var command = Create(state) with { ProjectId = SeedWorkspace.PumpProjectId };
        AssertError("project_access_denied", () => _engine.Apply(state, command, outsider, Now));
    }

    [Theory]
    [InlineData("engineer-maya")]
    [InlineData("not-a-member")]
    public void Work_assignments_must_reference_a_member_of_the_project_team(string assignee)
    {
        var state = Seed();
        AssertError("project_access_denied", () => _engine.Apply(state, Create(state) with { AssigneeId = assignee }, SeedWorkspace.Engineer, Now));
    }

    [Fact]
    public void Creation_preserves_validated_assignment_and_normalizes_text()
    {
        var state = Seed();
        var command = Create(state) with { Title = "  Review the ramp  ", Description = "  Check both directions.  " };
        var item = _engine.Apply(state, command, SeedWorkspace.Engineer, Now).State.WorkItems[^1];
        Assert.Equal("Review the ramp", item.Title);
        Assert.Equal("Check both directions.", item.Description);
        Assert.Equal(WorkItemStatus.Backlog, item.Status);
        Assert.Equal(DateOnly.FromDateTime(Now.UtcDateTime).AddDays(7), item.DueDate);
        Assert.Equal(SeedWorkspace.Engineer.Id, item.AssigneeId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("abc\0def")]
    public void Invalid_work_titles_are_rejected(string title)
    {
        var state = Seed();
        AssertError("validation_failed", () => _engine.Apply(state, Create(state) with { Title = title }, SeedWorkspace.Engineer, Now));
    }

    [Fact]
    public void Excessively_long_work_content_is_rejected()
    {
        var state = Seed();
        AssertError("validation_failed", () => _engine.Apply(state, Create(state) with { Title = new string('a', 141) }, SeedWorkspace.Engineer, Now));
        AssertError("validation_failed", () => _engine.Apply(state, Create(state) with { Description = new string('a', 2001) }, SeedWorkspace.Engineer, Now));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(732)]
    public void Work_due_dates_have_bounded_valid_ranges(int days)
    {
        var state = Seed();
        AssertError("validation_failed", () => _engine.Apply(state, Create(state) with { DueDate = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(days) }, SeedWorkspace.Engineer, Now));
    }

    [Fact]
    public void Unknown_priority_and_project_are_rejected()
    {
        var state = Seed();
        AssertError("validation_failed", () => _engine.Apply(state, Create(state) with { Priority = (WorkItemPriority)99 }, SeedWorkspace.Engineer, Now));
        AssertError("not_found", () => _engine.Apply(state, Create(state) with { ProjectId = "missing" }, SeedWorkspace.Engineer, Now));
    }

    public static IEnumerable<object[]> TransitionMatrix()
    {
        foreach (var from in Enum.GetValues<WorkItemStatus>())
        {
            foreach (var to in Enum.GetValues<WorkItemStatus>())
            {
                var permitted = (from, to) is
                    (WorkItemStatus.Backlog, WorkItemStatus.InProgress) or
                    (WorkItemStatus.InProgress, WorkItemStatus.Blocked) or
                    (WorkItemStatus.InProgress, WorkItemStatus.Review) or
                    (WorkItemStatus.Blocked, WorkItemStatus.InProgress) or
                    (WorkItemStatus.Review, WorkItemStatus.InProgress) or
                    (WorkItemStatus.Review, WorkItemStatus.Done) or
                    (WorkItemStatus.Done, WorkItemStatus.InProgress);
                yield return [from, to, permitted];
            }
        }
    }

    [Theory]
    [MemberData(nameof(TransitionMatrix))]
    public void Work_state_machine_prevents_skipped_reviews_and_invalid_moves(WorkItemStatus from, WorkItemStatus to, bool permitted)
    {
        var state = Seed();
        var item = state.WorkItems[0];
        state = state with { WorkItems = state.WorkItems.Replace(item, item with { Status = from }) };
        var command = new TransitionWorkItemCommand(state.Revision, item.Id, to);
        if (permitted)
        {
            var mutation = _engine.Apply(state, command, SeedWorkspace.Engineer, Now);
            Assert.Equal(to, mutation.State.WorkItems[0].Status);
            Assert.Equal("work.transitioned", mutation.AuditEntry.Action);
        }
        else
        {
            AssertError("invalid_transition", () => _engine.Apply(state, command, SeedWorkspace.Engineer, Now));
        }
    }

    [Fact]
    public void Unknown_work_and_drive_identifiers_fail_predictably()
    {
        var state = Seed();
        AssertError("not_found", () => _engine.Apply(state, new TransitionWorkItemCommand(0, "missing", WorkItemStatus.Review), SeedWorkspace.Engineer, Now));
        AssertError("not_found", () => _engine.Apply(state, new RunSimulationCommand(0, "missing"), SeedWorkspace.Engineer, Now));
        Assert.Empty(WorkspaceEngine.AllowedTransitions((WorkItemStatus)123));
    }

    [Fact]
    public void Approved_release_does_not_mutate_the_historical_configuration_or_candidate()
    {
        var state = Seed();
        var requested = RequestReadyRelease(state);
        var candidate = requested.Releases.Single();
        var mutation = _engine.Apply(requested, new ApproveReleaseCommand(requested.Revision, candidate.Id), SeedWorkspace.Approver, Now.AddMinutes(1));

        Assert.Equal(ReleaseStatus.Pending, candidate.Status);
        Assert.Equal(ReleaseStatus.Approved, mutation.State.Releases.Single().Status);
        Assert.Equal(SeedWorkspace.Approver.Id, mutation.State.Releases.Single().ReviewedBy);
        Assert.Equal("release.approved", mutation.AuditEntry.Action);
        Assert.Equal(state.Configurations, mutation.State.Configurations);
        Assert.Contains("no hardware deployment", mutation.State.Releases.Single().ReviewNote);
    }

    [Fact]
    public void High_or_critical_open_work_blocks_release()
    {
        var state = Seed();
        var readiness = WorkspaceEngine.EvaluateReleaseReadiness(state, SeedWorkspace.ConveyorProjectId);
        Assert.False(readiness.IsReady);
        Assert.False(readiness.Checks.Single(check => check.Code == "blocking-work").Passed);
        AssertError("release_not_ready", () => _engine.Apply(state, new RequestReleaseCommand(0, SeedWorkspace.ConveyorProjectId, "Please review this release"), SeedWorkspace.Engineer, Now));
    }

    [Fact]
    public void Normal_open_work_is_visible_but_does_not_block_release()
    {
        var state = Seed();
        Assert.Contains(state.WorkItems, item => item.ProjectId == SeedWorkspace.PumpProjectId && item.Priority == WorkItemPriority.Normal && item.Status != WorkItemStatus.Done);
        Assert.True(WorkspaceEngine.EvaluateReleaseReadiness(state, SeedWorkspace.PumpProjectId).IsReady);
    }

    [Fact]
    public void Empty_configuration_scope_cannot_be_marked_ready()
    {
        var state = Seed();
        state = state with { Configurations = state.Configurations.Where(configuration => configuration.ProjectId != SeedWorkspace.PumpProjectId).ToImmutableArray() };
        Assert.False(WorkspaceEngine.EvaluateReleaseReadiness(state, SeedWorkspace.PumpProjectId).IsReady);
    }

    [Fact]
    public void Duplicate_pending_requests_and_empty_notes_are_rejected()
    {
        var state = Seed();
        AssertError("validation_failed", () => _engine.Apply(state, new RequestReleaseCommand(0, SeedWorkspace.PumpProjectId, ""), SeedWorkspace.Engineer, Now));
        state = RequestReadyRelease(state);
        AssertError("release_already_pending", () => _engine.Apply(state, new RequestReleaseCommand(state.Revision, SeedWorkspace.PumpProjectId, "Another candidate"), SeedWorkspace.Engineer, Now));
    }

    [Fact]
    public void Engineer_cannot_approve_or_reject_their_own_request()
    {
        var state = RequestReadyRelease(Seed());
        var id = state.Releases.Single().Id;
        AssertError("forbidden", () => _engine.Apply(state, new ApproveReleaseCommand(state.Revision, id), SeedWorkspace.Engineer, Now));
        AssertError("forbidden", () => _engine.Apply(state, new RejectReleaseCommand(state.Revision, id, "Rejected by requestor"), SeedWorkspace.Engineer, Now));
    }

    [Fact]
    public void Requestor_cannot_self_approve_after_a_role_change()
    {
        var state = RequestReadyRelease(Seed());
        var member = state.Members.Single(member => member.Id == SeedWorkspace.Engineer.Id);
        state = state with { Members = state.Members.Replace(member, member with { Role = WorkspaceRole.Approver }) };
        AssertError("self_approval_forbidden", () => _engine.Apply(state,
            new ApproveReleaseCommand(state.Revision, state.Releases.Single().Id), SeedWorkspace.Engineer with { Role = WorkspaceRole.Approver }, Now));
    }

    [Fact]
    public void Configuration_author_cannot_approve_someone_elses_request()
    {
        var state = Seed();
        var configuration = state.Configurations.Single(configuration => configuration.Id == SeedWorkspace.PumpDriveId);
        state = state with { Configurations = state.Configurations.Replace(configuration, configuration with { UpdatedBy = SeedWorkspace.Approver.Id }) };
        state = RequestReadyRelease(state);
        AssertError("self_approval_forbidden", () => _engine.Apply(state,
            new ApproveReleaseCommand(state.Revision, state.Releases.Single().Id), SeedWorkspace.Approver, Now));
    }

    [Fact]
    public void Rejection_requires_a_reason_and_is_audited()
    {
        var state = RequestReadyRelease(Seed());
        var release = state.Releases.Single();
        AssertError("validation_failed", () => _engine.Apply(state, new RejectReleaseCommand(state.Revision, release.Id, ""), SeedWorkspace.Approver, Now));
        var mutation = _engine.Apply(state, new RejectReleaseCommand(state.Revision, release.Id, "Add independent stop-sequence evidence."), SeedWorkspace.Approver, Now);
        Assert.Equal(ReleaseStatus.Rejected, mutation.State.Releases.Single().Status);
        Assert.Equal("release.rejected", mutation.AuditEntry.Action);
        Assert.Contains("stop-sequence", mutation.State.Releases.Single().ReviewNote);
    }

    [Fact]
    public void Release_cannot_be_reviewed_twice()
    {
        var state = RequestReadyRelease(Seed());
        var release = state.Releases.Single();
        state = _engine.Apply(state, new ApproveReleaseCommand(state.Revision, release.Id), SeedWorkspace.Approver, Now).State;
        AssertError("release_already_reviewed", () => _engine.Apply(state, new ApproveReleaseCommand(state.Revision, release.Id), SeedWorkspace.Approver, Now));
        AssertError("not_found", () => _engine.Apply(state, new ApproveReleaseCommand(state.Revision, "missing"), SeedWorkspace.Approver, Now));
    }

    [Fact]
    public void Configuration_change_invalidates_pending_candidate_and_prior_evidence()
    {
        var state = RequestReadyRelease(Seed());
        var originalRunCount = state.SimulationRuns.Length;
        var releaseId = state.Releases.Single().Id;
        state = _engine.Apply(state, Configure(state, SeedWorkspace.PumpDriveId) with { TargetSpeedRpm = 1900 }, SeedWorkspace.Engineer, Now).State;
        Assert.Equal(ReleaseStatus.Stale, state.Releases.Single().Status);
        Assert.False(WorkspaceEngine.EvaluateReleaseReadiness(state, SeedWorkspace.PumpProjectId).IsReady);
        Assert.Equal(originalRunCount, state.SimulationRuns.Length);
        Assert.Equal(2, state.Configurations.Single(configuration => configuration.Id == SeedWorkspace.PumpDriveId).Revision);
        AssertError("release_stale", () => _engine.Apply(state, new ApproveReleaseCommand(state.Revision, releaseId), SeedWorkspace.Approver, Now));
    }

    [Fact]
    public void Work_and_evidence_changes_invalidate_a_pending_candidate()
    {
        var requested = RequestReadyRelease(Seed());
        var workChanged = _engine.Apply(requested,
            new TransitionWorkItemCommand(requested.Revision, "work-0008", WorkItemStatus.Done), SeedWorkspace.Engineer, Now).State;
        Assert.Equal(ReleaseStatus.Stale, workChanged.Releases.Single().Status);
        var evidenceChanged = _engine.Apply(requested,
            new RunSimulationCommand(requested.Revision, SeedWorkspace.PumpDriveId), SeedWorkspace.Engineer, Now).State;
        Assert.Equal(ReleaseStatus.Stale, evidenceChanged.Releases.Single().Status);
    }

    [Fact]
    public void Unrelated_project_change_does_not_invalidate_candidate()
    {
        var state = RequestReadyRelease(Seed());
        state = _engine.Apply(state, Create(state), SeedWorkspace.Engineer, Now).State;
        Assert.Equal(ReleaseStatus.Pending, state.Releases.Single().Status);
        var mutation = _engine.Apply(state, new ApproveReleaseCommand(state.Revision, state.Releases.Single().Id), SeedWorkspace.Approver, Now);
        Assert.Equal(ReleaseStatus.Approved, mutation.State.Releases.Single().Status);
    }

    [Fact]
    public void Fingerprint_recheck_catches_a_changed_snapshot_even_without_status_invalidation()
    {
        var state = RequestReadyRelease(Seed());
        var configuration = state.Configurations.Single(configuration => configuration.Id == SeedWorkspace.PumpDriveId);
        state = state with { Configurations = state.Configurations.Replace(configuration, configuration with { TargetSpeedRpm = 1900 }) };
        Assert.Equal(ReleaseStatus.Pending, state.Releases.Single().Status);
        AssertError("release_stale", () => _engine.Apply(state,
            new ApproveReleaseCommand(state.Revision, state.Releases.Single().Id), SeedWorkspace.Approver, Now));
    }

    [Fact]
    public void New_evidence_and_new_request_can_replace_a_stale_candidate()
    {
        var state = RequestReadyRelease(Seed());
        state = _engine.Apply(state, Configure(state, SeedWorkspace.PumpDriveId) with { TargetSpeedRpm = 1900 }, SeedWorkspace.Engineer, Now).State;
        state = _engine.Apply(state, new RunSimulationCommand(state.Revision, SeedWorkspace.PumpDriveId), SeedWorkspace.Engineer, Now).State;
        state = RequestReadyRelease(state);
        Assert.Equal(2, state.Releases.Length);
        Assert.Equal(ReleaseStatus.Stale, state.Releases[0].Status);
        Assert.Equal(ReleaseStatus.Pending, state.Releases[1].Status);
    }

    [Fact]
    public void Workspace_capacity_prevents_unbounded_work_growth()
    {
        var state = Seed();
        state = state with { WorkItems = Enumerable.Range(1, 1000).Select(number => state.WorkItems[0] with { Id = $"item-{number}" }).ToImmutableArray() };
        AssertError("workspace_capacity_reached", () => _engine.Apply(state, Create(state), SeedWorkspace.Engineer, Now));
    }

    [Fact]
    public void Snapshot_and_polymorphic_commands_round_trip_without_losing_types()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        var state = Seed();
        var encoded = JsonSerializer.Serialize(state, options);
        var decoded = JsonSerializer.Deserialize<WorkspaceState>(encoded, options);
        Assert.NotNull(decoded);
        Assert.Equal(encoded, JsonSerializer.Serialize(decoded, options));
        foreach (var row in WriteCommands())
        {
            var command = (WorkspaceCommand)row[0];
            var json = JsonSerializer.Serialize(command, options);
            Assert.Contains("\"type\":", json);
            Assert.Equal(command, JsonSerializer.Deserialize<WorkspaceCommand>(json, options));
        }
    }

    private WorkspaceState RequestReadyRelease(WorkspaceState state) => _engine.Apply(state,
        new RequestReleaseCommand(state.Revision, SeedWorkspace.PumpProjectId, "Current revision has passing commissioning evidence."), SeedWorkspace.Engineer, Now).State;

    internal static CreateWorkItemCommand Create(WorkspaceState state) =>
        new(state.Revision, SeedWorkspace.ConveyorProjectId, "Review drive tuning", "Confirm target speed and current envelope.", SeedWorkspace.Engineer.Id, WorkItemPriority.Normal);

    internal static UpdateDriveConfigurationCommand Configure(WorkspaceState state, string configurationId = SeedWorkspace.ConveyorDriveId)
    {
        var configuration = state.Configurations.Single(configuration => configuration.Id == configurationId);
        return new(state.Revision, configuration.Id, configuration.TargetSpeedRpm, configuration.AccelerationSeconds,
            configuration.DecelerationSeconds, configuration.CurrentLimitAmps, configuration.LoadPercent);
    }

    internal static void AssertError(string code, Action operation)
    {
        var error = Assert.Throws<WorkspaceException>(operation);
        Assert.Equal(code, error.Code);
    }
}
