using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Teammy.Application;
using Teammy.Domain;
using Teammy.Infrastructure;

namespace Teammy.Api.Tests;

public sealed class WorkspaceApiTests
{
    private static JsonSerializerOptions Json => WorkspaceRepository.JsonOptions;

    [Theory]
    [InlineData("/api/workspace")]
    [InlineData("/api/audit")]
    [InlineData("/api/me")]
    [InlineData("/openapi/v1.json")]
    public async Task WorkspaceEndpointsRequireAnAuthenticatedIdentity(string path)
    {
        using var app = new ApiFactory();
        using var client = app.Client(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task ReadReturnsTypedStateIdentityAndConcurrencyToken()
    {
        using var app = new ApiFactory();
        using var client = app.Client("Viewer");
        var response = await client.GetAsync("/api/workspace");
        response.EnsureSuccessStatusCode();
        var state = await response.Content.ReadFromJsonAsync<WorkspaceState>(Json);
        Assert.Equal(0, state!.Revision);
        Assert.Equal(2, state.Projects.Length);
        Assert.Equal("\"0\"", response.Headers.ETag!.Tag);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.True(response.Headers.Contains("X-Trace-Id"));
        Assert.Equal(SeedWorkspace.Viewer, await client.GetFromJsonAsync<Actor>("/api/me", Json));
    }

    [Fact]
    public async Task RejectedViewerWriteDoesNotChangeStateOrAudit()
    {
        using var app = new ApiFactory();
        using var client = app.Client("Viewer");
        var response = await Send(client, NewWork(0));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, (await State(client)).Revision);
        Assert.Empty((await client.GetFromJsonAsync<AuditEntry[]>("/api/audit", Json))!);
    }

    [Fact]
    public async Task ConcurrentWritesProduceOneCommitAndOneConflict()
    {
        using var app = new ApiFactory();
        using var first = app.Client();
        using var second = app.Client();
        var responses = await Task.WhenAll(Send(first, NewWork(0, "First competing change")),
            Send(second, NewWork(0, "Second competing change")));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        var state = await State(first);
        Assert.Equal(1, state.Revision);
        Assert.Equal(9, state.WorkItems.Length);
        var audit = await first.GetFromJsonAsync<AuditEntry[]>("/api/audit", Json);
        Assert.Single(audit!);
        Assert.Equal(SeedWorkspace.Engineer.Id, audit![0].ActorId);
        Assert.Equal(1, audit[0].Revision);
    }

    [Theory]
    [InlineData("{\"type\":\"createWorkItem\",\"expectedRevision\":0,\"projectId\":\"project-conveyor\",\"title\":\"Injected identity\",\"description\":\"\",\"assigneeId\":\"engineer-anirudh\",\"priority\":\"Normal\",\"actor\":{\"role\":\"Approver\"}}")]
    [InlineData("{\"type\":\"createWorkItem\",\"expectedRevision\":0,\"projectId\":\"project-conveyor\",\"title\":\"Unknown integer enum\",\"description\":\"\",\"assigneeId\":\"engineer-anirudh\",\"priority\":999}")]
    [InlineData("{\"type\":\"runSimulation\",\"configurationId\":\"drive-conveyor\"}")]
    [InlineData("{\"type\":\"deleteEverything\",\"expectedRevision\":0}")]
    [InlineData("not json")]
    public async Task MalformedOrForgedCommandsAreRejectedBeforeMutation(string payload)
    {
        using var app = new ApiFactory();
        using var client = app.Client();
        var response = await client.PostAsync("/api/commands", new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, (await State(client)).Revision);
        Assert.Empty((await client.GetFromJsonAsync<AuditEntry[]>("/api/audit", Json))!);
    }

    [Fact]
    public async Task ParameterValidationReturnsProblemDetailsAndPreservesState()
    {
        using var app = new ApiFactory();
        using var client = app.Client();
        var response = await Send(client, new UpdateDriveConfigurationCommand(0, SeedWorkspace.ConveyorDriveId,
            99_999, 4, 5, 11, 50));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.TryGetProperty("code", out _));
        Assert.True(problem.TryGetProperty("traceId", out _));
        Assert.Equal(0, (await State(client)).Revision);
    }

    [Fact]
    public async Task CompleteCommissioningFlowRequiresEvidenceAndIndependentReview()
    {
        using var app = new ApiFactory();
        using var engineer = app.Client();
        using var approver = app.Client("Approver");
        var blocked = await Send(engineer, new RequestReleaseCommand(0, SeedWorkspace.ConveyorProjectId, "Release packaging line"));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        await Success(engineer, new TransitionWorkItemCommand(0, "work-0001", WorkItemStatus.Review));
        await Success(engineer, new TransitionWorkItemCommand(1, "work-0001", WorkItemStatus.Done));
        await Success(engineer, new TransitionWorkItemCommand(2, "work-0002", WorkItemStatus.Done));
        var simulated = await Success(engineer, new RunSimulationCommand(3, SeedWorkspace.ConveyorDriveId));
        Assert.True(simulated.SimulationRuns.Last().Passed);
        var requested = await Success(engineer, new RequestReleaseCommand(4, SeedWorkspace.ConveyorProjectId, "Evidence reviewed and work completed."));
        var release = requested.Releases.Single();
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(engineer, new ApproveReleaseCommand(5, release.Id))).StatusCode);
        var approved = await Success(approver, new ApproveReleaseCommand(5, release.Id));
        Assert.Equal(ReleaseStatus.Approved, approved.Releases.Single().Status);
        Assert.Equal(SeedWorkspace.Approver.Id, approved.Releases.Single().ReviewedBy);
        var audit = (await engineer.GetFromJsonAsync<AuditEntry[]>("/api/audit", Json))!;
        Assert.Equal(6, audit.Length);
        Assert.Equal("release.approved", audit[0].Action);
        Assert.Equal(Enumerable.Range(1, 6).Reverse(), audit.Select(entry => entry.Revision));
    }

    [Fact]
    public async Task ConfigurationChangeInvalidatesPendingCandidateAndOldEvidence()
    {
        using var app = new ApiFactory();
        using var engineer = app.Client();
        using var approver = app.Client("Approver");
        var requested = await Success(engineer, new RequestReleaseCommand(0, SeedWorkspace.PumpProjectId, "Approve cooling loop readiness."));
        var config = requested.Configurations.Single(item => item.Id == SeedWorkspace.PumpDriveId);
        var updated = await Success(engineer, new UpdateDriveConfigurationCommand(1, config.Id,
            1600, config.AccelerationSeconds, config.DecelerationSeconds, config.CurrentLimitAmps, config.LoadPercent));
        Assert.Equal(ReleaseStatus.Stale, updated.Releases.Single().Status);
        var response = await Send(approver, new ApproveReleaseCommand(2, requested.Releases.Single().Id));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(WorkspaceEngine.EvaluateReleaseReadiness(updated, SeedWorkspace.PumpProjectId).IsReady);
    }

    [Fact]
    public async Task StateAndAuditSurviveApplicationRestart()
    {
        string path;
        using (var firstApp = new ApiFactory(deleteDatabase: false))
        {
            path = firstApp.DatabasePath;
            using var first = firstApp.Client();
            await Success(first, NewWork(0, "Persist this work across restart"));
        }

        using var secondApp = new ApiFactory(path);
        using var second = secondApp.Client();
        Assert.Equal(1, (await State(second)).Revision);
        Assert.Contains((await State(second)).WorkItems, item => item.Title == "Persist this work across restart");
        Assert.Single((await second.GetFromJsonAsync<AuditEntry[]>("/api/audit", Json))!);
    }

    [Fact]
    public async Task AuditCursorAndLimitProduceStablePages()
    {
        using var app = new ApiFactory();
        using var client = app.Client();
        await Success(client, NewWork(0));
        await Success(client, NewWork(1));
        await Success(client, NewWork(2));
        var page = (await client.GetFromJsonAsync<AuditEntry[]>("/api/audit?limit=1&beforeRevision=3", Json))!;
        Assert.Single(page);
        Assert.Equal(2, page[0].Revision);
    }

    [Fact]
    public async Task ReadinessDetectsStorageFailureWithoutFailingLiveness()
    {
        using var app = new ApiFactory();
        using var client = app.Client(null);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        await using var connection = new SqliteConnection($"Data Source={app.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE workspaces";
        await command.ExecuteNonQueryAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    private static CreateWorkItemCommand NewWork(int revision, string title = "Verify API commissioning scenario") =>
        new(revision, SeedWorkspace.ConveyorProjectId, title, "Integration test work item.", SeedWorkspace.Engineer.Id, WorkItemPriority.Normal);
    private static Task<HttpResponseMessage> Send(HttpClient client, WorkspaceCommand command) =>
        client.PostAsJsonAsync("/api/commands", command, Json);
    private static async Task<WorkspaceState> State(HttpClient client) =>
        (await client.GetFromJsonAsync<WorkspaceState>("/api/workspace", Json))!;
    private static async Task<WorkspaceState> Success(HttpClient client, WorkspaceCommand command)
    {
        var response = await Send(client, command);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<WorkspaceState>(Json))!;
    }
}
