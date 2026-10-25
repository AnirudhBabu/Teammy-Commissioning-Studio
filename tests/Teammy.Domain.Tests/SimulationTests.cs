using System.Collections.Immutable;
using System.Text.Json;
using Teammy.Application;
using Teammy.Domain;

namespace Teammy.Domain.Tests;

public sealed class SimulationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);
    private static WorkspaceState Seed() => SeedWorkspace.Create(Now);

    [Theory]
    [InlineData("speed", 0)]
    [InlineData("speed", 3000.01)]
    [InlineData("speed", double.NaN)]
    [InlineData("speed", double.PositiveInfinity)]
    [InlineData("acceleration", 0)]
    [InlineData("acceleration", 0.249)]
    [InlineData("acceleration", 120.01)]
    [InlineData("acceleration", double.NaN)]
    [InlineData("deceleration", -1)]
    [InlineData("deceleration", 120.01)]
    [InlineData("deceleration", double.NegativeInfinity)]
    [InlineData("current", 0)]
    [InlineData("current", 15.01)]
    [InlineData("current", double.NaN)]
    [InlineData("load", -0.01)]
    [InlineData("load", 100.01)]
    [InlineData("load", double.NaN)]
    [InlineData("load", double.PositiveInfinity)]
    public void Configuration_rejects_nonphysical_nonfinite_and_out_of_profile_values(string parameter, double value)
    {
        var state = Seed();
        var command = Replace(WorkspaceEngineTests.Configure(state), parameter, value);
        WorkspaceEngineTests.AssertError("validation_failed", () => new WorkspaceEngine().Apply(state, command, SeedWorkspace.Engineer, Now));
        Assert.Equal(1, state.Configurations[0].Revision);
    }

    [Theory]
    [InlineData("speed", 1)]
    [InlineData("speed", 3000)]
    [InlineData("acceleration", 0.25)]
    [InlineData("acceleration", 120)]
    [InlineData("deceleration", 0.25)]
    [InlineData("deceleration", 120)]
    [InlineData("current", 0.1)]
    [InlineData("current", 15)]
    [InlineData("load", 0)]
    [InlineData("load", 100)]
    public void Configuration_boundary_values_can_be_saved_but_require_simulation(string parameter, double value)
    {
        var state = Seed();
        var command = Replace(WorkspaceEngineTests.Configure(state), parameter, value);
        var mutation = new WorkspaceEngine().Apply(state, command, SeedWorkspace.Engineer, Now);
        Assert.Equal(2, mutation.State.Configurations[0].Revision);
        Assert.Equal(SeedWorkspace.Engineer.Id, mutation.State.Configurations[0].UpdatedBy);
        Assert.Equal("configuration.updated", mutation.AuditEntry.Action);
    }

    [Fact]
    public void Nominal_run_is_deterministic_traceable_and_complete()
    {
        var state = Seed();
        var engine = new WorkspaceEngine();
        var command = new RunSimulationCommand(0, SeedWorkspace.ConveyorDriveId);
        var first = engine.Apply(state, command, SeedWorkspace.Engineer, Now);
        var second = engine.Apply(state, command, SeedWorkspace.Engineer, Now);
        var result = first.State.SimulationRuns[^1];

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.True(result.Passed);
        Assert.Equal(SeedWorkspace.ConveyorDriveId, result.ConfigurationId);
        Assert.Equal(1, result.ConfigurationRevision);
        Assert.Equal(SeedWorkspace.Engineer.Id, result.RequestedBy);
        Assert.Equal(Now, result.CompletedAt);
        Assert.Equal(4, result.Checks.Length);
        Assert.All(result.Checks, check => Assert.True(check.Passed));
        Assert.Equal(25, result.Samples.Length);
        Assert.Equal(0, result.Samples[0].SpeedRpm);
        Assert.Equal(0, result.Samples[^1].SpeedRpm);
        Assert.Equal(1450, result.Samples.Max(sample => sample.SpeedRpm));
        Assert.True(result.Samples.Zip(result.Samples.Skip(1)).All(pair => pair.First.Seconds < pair.Second.Seconds));
        Assert.Equal("simulation.completed", first.AuditEntry.Action);
    }

    [Theory]
    [InlineData("acceleration", 0.25, "acceleration-envelope")]
    [InlineData("deceleration", 0.25, "deceleration-envelope")]
    [InlineData("current", 0.1, "current-headroom")]
    public void Simulation_fails_when_a_saved_configuration_exceeds_an_operating_envelope(string parameter, double value, string failingCheck)
    {
        var state = Seed();
        var engine = new WorkspaceEngine();
        state = engine.Apply(state, Replace(WorkspaceEngineTests.Configure(state), parameter, value), SeedWorkspace.Engineer, Now).State;
        state = engine.Apply(state, new RunSimulationCommand(state.Revision, SeedWorkspace.ConveyorDriveId), SeedWorkspace.Engineer, Now).State;
        var run = state.SimulationRuns[^1];
        Assert.False(run.Passed);
        Assert.False(run.Checks.Single(check => check.Code == failingCheck).Passed);
        Assert.Equal(2, run.ConfigurationRevision);
    }

    [Fact]
    public void Heavy_load_can_exceed_thermal_envelope_despite_sufficient_current_limit()
    {
        var state = Seed();
        var engine = new WorkspaceEngine();
        var configuration = WorkspaceEngineTests.Configure(state, SeedWorkspace.PumpDriveId) with { LoadPercent = 100, CurrentLimitAmps = 6.25 };
        state = engine.Apply(state, configuration, SeedWorkspace.Engineer, Now).State;
        state = engine.Apply(state, new RunSimulationCommand(state.Revision, SeedWorkspace.PumpDriveId), SeedWorkspace.Engineer, Now).State;
        var run = state.SimulationRuns[^1];
        Assert.False(run.Passed);
        Assert.True(run.Checks.Single(check => check.Code == "current-headroom").Passed);
        Assert.False(run.Checks.Single(check => check.Code == "thermal-envelope").Passed);
        Assert.False(WorkspaceEngine.EvaluateReleaseReadiness(state, SeedWorkspace.PumpProjectId).IsReady);
    }

    [Fact]
    public void Old_passing_run_never_satisfies_a_new_configuration_revision()
    {
        var state = Seed();
        Assert.True(WorkspaceEngine.EvaluateReleaseReadiness(state, SeedWorkspace.PumpProjectId).IsReady);
        state = new WorkspaceEngine().Apply(state,
            WorkspaceEngineTests.Configure(state, SeedWorkspace.PumpDriveId), SeedWorkspace.Engineer, Now).State;
        Assert.False(WorkspaceEngine.EvaluateReleaseReadiness(state, SeedWorkspace.PumpProjectId).IsReady);
        Assert.Contains(state.SimulationRuns, run => run.ConfigurationId == SeedWorkspace.PumpDriveId && run.Passed);
    }

    [Fact]
    public void Latest_failed_run_overrides_an_older_pass_on_the_same_revision()
    {
        var state = Seed();
        var engine = new WorkspaceEngine(new FailedSimulator());
        state = engine.Apply(state, new RunSimulationCommand(state.Revision, SeedWorkspace.PumpDriveId), SeedWorkspace.Engineer, Now).State;
        Assert.False(WorkspaceEngine.EvaluateReleaseReadiness(state, SeedWorkspace.PumpProjectId).IsReady);
    }

    [Fact]
    public void Evidence_with_mismatched_revision_cannot_enter_the_workspace()
    {
        var state = Seed();
        var engine = new WorkspaceEngine(new InconsistentSimulator());
        WorkspaceEngineTests.AssertError("invalid_simulation_evidence", () => engine.Apply(state,
            new RunSimulationCommand(state.Revision, SeedWorkspace.PumpDriveId), SeedWorkspace.Engineer, Now));
    }

    [Fact]
    public void Simulation_cannot_access_a_different_team_project()
    {
        var state = Seed();
        WorkspaceEngineTests.AssertError("project_access_denied", () => new WorkspaceEngine().Apply(state,
            new RunSimulationCommand(0, SeedWorkspace.PumpDriveId), new Actor("engineer-mateo", "Mateo Ruiz", WorkspaceRole.Engineer), Now));
    }

    [Fact]
    public void Seed_has_consistent_references_and_both_ready_and_blocked_demo_paths()
    {
        var state = Seed();
        Assert.Equal(2, state.Projects.Length);
        Assert.All(state.WorkItems, item =>
        {
            var project = Assert.Single(state.Projects, project => project.Id == item.ProjectId);
            var team = Assert.Single(state.Teams, team => team.Id == project.TeamId);
            Assert.Contains(item.AssigneeId, team.MemberIds);
        });
        Assert.All(state.Configurations, configuration =>
        {
            Assert.Contains(state.DriveProfiles, profile => profile.Id == configuration.ProfileId);
            Assert.Contains(state.Members, member => member.Id == configuration.UpdatedBy);
        });
        Assert.True(WorkspaceEngine.EvaluateReleaseReadiness(state, SeedWorkspace.PumpProjectId).IsReady);
        Assert.False(WorkspaceEngine.EvaluateReleaseReadiness(state, SeedWorkspace.ConveyorProjectId).IsReady);
    }

    [Fact]
    public void Simulation_and_candidate_history_have_explicit_capacity_bounds()
    {
        var state = Seed();
        var manyRuns = state with { SimulationRuns = Enumerable.Repeat(state.SimulationRuns[0], 2000).ToImmutableArray() };
        WorkspaceEngineTests.AssertError("workspace_capacity_reached", () => new WorkspaceEngine().Apply(manyRuns,
            new RunSimulationCommand(manyRuns.Revision, SeedWorkspace.ConveyorDriveId), SeedWorkspace.Engineer, Now));
        var requested = new WorkspaceEngine().Apply(state,
            new RequestReleaseCommand(state.Revision, SeedWorkspace.PumpProjectId, "Ready for independent review"), SeedWorkspace.Engineer, Now).State;
        requested = requested with { Releases = Enumerable.Repeat(requested.Releases[0] with { Status = ReleaseStatus.Rejected }, 1000).ToImmutableArray() };
        WorkspaceEngineTests.AssertError("workspace_capacity_reached", () => new WorkspaceEngine().Apply(requested,
            new RequestReleaseCommand(requested.Revision, SeedWorkspace.PumpProjectId, "Ready for independent review"), SeedWorkspace.Engineer, Now));
    }

    private static UpdateDriveConfigurationCommand Replace(UpdateDriveConfigurationCommand command, string parameter, double value) => parameter switch
    {
        "speed" => command with { TargetSpeedRpm = value },
        "acceleration" => command with { AccelerationSeconds = value },
        "deceleration" => command with { DecelerationSeconds = value },
        "current" => command with { CurrentLimitAmps = value },
        "load" => command with { LoadPercent = value },
        _ => throw new ArgumentOutOfRangeException(nameof(parameter))
    };

    private sealed class FailedSimulator : IDriveSimulator
    {
        public SimulationRun Simulate(DriveConfiguration configuration, DriveProfile profile, string runId, Actor actor, DateTimeOffset now)
        {
            var result = new DeterministicDriveSimulator().Simulate(configuration, profile, runId, actor, now);
            return result with { Passed = false, Checks = [new SimulationCheck("test-fault", "Injected fault", false, "Deterministic failing adapter.")] };
        }
    }

    private sealed class InconsistentSimulator : IDriveSimulator
    {
        public SimulationRun Simulate(DriveConfiguration configuration, DriveProfile profile, string runId, Actor actor, DateTimeOffset now) =>
            new DeterministicDriveSimulator().Simulate(configuration, profile, runId, actor, now) with { ConfigurationRevision = configuration.Revision - 1 };
    }
}
