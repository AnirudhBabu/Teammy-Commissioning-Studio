using System.Collections.Immutable;
using Teammy.Domain;

namespace Teammy.Application;

/// <summary>Explicit synthetic lab data. No real users, drives, sites, credentials, or deployments.</summary>
public static class SeedWorkspace
{
    public static Actor Engineer { get; } = new("engineer-anirudh", "Anirudh Babu", WorkspaceRole.Engineer);
    public static Actor Approver { get; } = new("approver-elena", "Elena Torres", WorkspaceRole.Approver);
    public static Actor Viewer { get; } = new("viewer-guest", "Guest reviewer", WorkspaceRole.Viewer);
    public const string ConveyorProjectId = "project-conveyor";
    public const string PumpProjectId = "project-pump";
    public const string ConveyorDriveId = "drive-conveyor";
    public const string IndexerDriveId = "drive-indexer";
    public const string PumpDriveId = "drive-pump";

    public static WorkspaceState Create(DateTimeOffset? now = null)
    {
        var timestamp = now ?? new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        var today = DateOnly.FromDateTime(timestamp.UtcDateTime);
        var members = ImmutableArray.Create(
            new Member(Engineer.Id, Engineer.DisplayName, Engineer.Role),
            new Member(Approver.Id, Approver.DisplayName, Approver.Role),
            new Member(Viewer.Id, Viewer.DisplayName, Viewer.Role),
            new Member("engineer-mateo", "Mateo Ruiz", WorkspaceRole.Engineer),
            new Member("engineer-maya", "Maya Chen", WorkspaceRole.Engineer));
        var teams = ImmutableArray.Create(
            new Team("team-motion", "Motion engineering", [Engineer.Id, Approver.Id, Viewer.Id, "engineer-mateo"]),
            new Team("team-platform", "Platform & reliability", [Engineer.Id, Approver.Id, Viewer.Id, "engineer-maya"]));
        var projects = ImmutableArray.Create(
            new EngineeringProject(ConveyorProjectId, "team-motion", "Packaging line commissioning",
                "Validate conveyor and indexing drive parameters, close engineering work, and assemble evidence for independent review.", "Barcelona lab / Cell 04"),
            new EngineeringProject(PumpProjectId, "team-platform", "Cooling loop upgrade",
                "Commission the simulated coolant pump drive with traceable configuration changes and release evidence.", "Barcelona lab / Utilities"));
        var profiles = ImmutableArray.Create(
            new DriveProfile("profile-vfd-12", "VFD · 12 A / 3,000 rpm", 3000, 12, 1000, 85),
            new DriveProfile("profile-servo-6", "Servo · 6 A / 6,000 rpm", 6000, 6, 1600, 80),
            new DriveProfile("profile-pump-5", "Pump · 5 A / 3,600 rpm", 3600, 5, 800, 85));
        var configurations = ImmutableArray.Create(
            new DriveConfiguration(ConveyorDriveId, ConveyorProjectId, "Conveyor / Axis A", profiles[0].Id,
                1450, 4, 5, 11.5, 55, 1, Engineer.Id, timestamp.AddHours(-8)),
            new DriveConfiguration(IndexerDriveId, ConveyorProjectId, "Indexer / Axis B", profiles[1].Id,
                2400, 4, 4, 6.8, 60, 1, "engineer-mateo", timestamp.AddHours(-6)),
            new DriveConfiguration(PumpDriveId, PumpProjectId, "Cooling pump / P-02", profiles[2].Id,
                1750, 5, 6, 4.8, 60, 1, "engineer-maya", timestamp.AddHours(-5)));
        WorkItem Work(int number, string project, string title, string description, string assignee,
            WorkItemPriority priority, WorkItemStatus status, int dueDays) => new(
                $"work-{number:D4}", $"TC-{number:D3}", project, title, description, assignee, priority, status,
                today.AddDays(dueDays), timestamp.AddDays(-5), timestamp.AddHours(-number));

        var workItems = ImmutableArray.Create(
            Work(1, ConveyorProjectId, "Validate conveyor ramp envelope", "Check acceleration and deceleration against the simulated drive profile.",
                Engineer.Id, WorkItemPriority.Critical, WorkItemStatus.InProgress, 1),
            Work(2, ConveyorProjectId, "Review independent stop sequence", "Record the engineering review before candidate submission. No connection to physical equipment.",
                "engineer-mateo", WorkItemPriority.High, WorkItemStatus.Review, 1),
            Work(3, ConveyorProjectId, "Capture commissioning evidence", "Run both simulated drives and retain the configuration revision with each result.",
                Engineer.Id, WorkItemPriority.Normal, WorkItemStatus.Backlog, 2),
            Work(4, ConveyorProjectId, "Confirm instrumentation checklist", "The simulated lab profile and telemetry channels have been reviewed.",
                "engineer-mateo", WorkItemPriority.High, WorkItemStatus.Done, 0),
            Work(5, ConveyorProjectId, "Review operator handover notes", "Waiting for the documentation review window.",
                "engineer-mateo", WorkItemPriority.Low, WorkItemStatus.Blocked, 4),
            Work(6, PumpProjectId, "Verify pump current headroom", "Review the modelled peak against the configured current limit.",
                "engineer-maya", WorkItemPriority.Critical, WorkItemStatus.Done, 0),
            Work(7, PumpProjectId, "Check commissioning runbook", "Confirm recovery procedure and independent review ownership.",
                Engineer.Id, WorkItemPriority.High, WorkItemStatus.Done, 0),
            Work(8, PumpProjectId, "Archive operator notes", "Attach the final handover notes after the release review.",
                "engineer-maya", WorkItemPriority.Normal, WorkItemStatus.Review, 3));
        var simulator = new DeterministicDriveSimulator();
        var evidence = ImmutableArray.Create(
            simulator.Simulate(configurations[1], profiles[1], "seed-simulation-indexer", Engineer, timestamp.AddHours(-4)),
            simulator.Simulate(configurations[2], profiles[2], "seed-simulation-pump", Engineer, timestamp.AddHours(-3)));
        return new WorkspaceState(0, teams, members, projects, workItems, profiles, configurations, evidence, []);
    }
}
