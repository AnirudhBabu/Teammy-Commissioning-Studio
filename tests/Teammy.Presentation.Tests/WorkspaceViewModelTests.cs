using Teammy.Application;
using Teammy.Domain;
using Teammy.Presentation.Clients;
using Teammy.Presentation.ViewModels;
using Xunit;

namespace Teammy.Presentation.Tests;

public sealed class WorkspaceViewModelTests
{
    [Fact]
    public async Task InitialLoadSelectsAProjectWithRealReadinessAndAnEngineerIdentity()
    {
        using var viewModel = new WorkspaceViewModel(new DemoWorkspaceClient());
        await viewModel.InitializeAsync();
        Assert.True(viewModel.HasWorkspace);
        Assert.Equal(SeedWorkspace.ConveyorProjectId, viewModel.SelectedProject?.Id);
        Assert.Equal(2, viewModel.DriveCount);
        Assert.Equal(2, viewModel.BlockingWorkItemCount);
        Assert.False(viewModel.IsReleaseReady);
        Assert.True(viewModel.CanEngineer);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task CreatingWorkCommitsOnceAndClearsTheDraftOnlyOnSuccess()
    {
        using var viewModel = new WorkspaceViewModel(new DemoWorkspaceClient());
        await viewModel.InitializeAsync();
        var count = viewModel.WorkItems.Count;
        viewModel.NewWorkItemTitle = "  Review current headroom  ";
        viewModel.NewWorkItemDescription = "Capture a repeatable test case.";
        await viewModel.CreateWorkItemCommand.ExecuteAsync();
        Assert.False(viewModel.HasError);
        Assert.Equal(count + 1, viewModel.WorkItems.Count);
        Assert.Contains(viewModel.WorkItems, item => item.Title == "Review current headroom");
        Assert.Equal("", viewModel.NewWorkItemTitle);
        Assert.Equal(1, viewModel.Revision);
        Assert.Single(viewModel.AuditEntries);
    }

    [Fact]
    public async Task RejectedWorkItemKeepsTheUsersDraftAndDoesNotAdvanceTheRevision()
    {
        using var viewModel = new WorkspaceViewModel(new DemoWorkspaceClient());
        await viewModel.InitializeAsync();
        viewModel.NewWorkItemTitle = new string('x', 500);
        await viewModel.CreateWorkItemCommand.ExecuteAsync();
        Assert.True(viewModel.HasError);
        Assert.Equal(500, viewModel.NewWorkItemTitle.Length);
        Assert.Equal(0, viewModel.Revision);
        Assert.Empty(viewModel.AuditEntries);
    }

    [Fact]
    public async Task WorkboardOffersOnlyLegalTransitionsAndUpdatesItsColumns()
    {
        using var viewModel = new WorkspaceViewModel(new DemoWorkspaceClient());
        await viewModel.InitializeAsync();
        viewModel.SelectedWorkItem = viewModel.WorkItems.Single(item => item.Id == "work-0001");
        Assert.Equal([WorkItemStatus.Blocked, WorkItemStatus.Review], viewModel.WorkItemStatuses);
        viewModel.TargetWorkItemStatus = WorkItemStatus.Review;
        await viewModel.TransitionWorkItemCommand.ExecuteAsync();
        Assert.False(viewModel.HasError);
        Assert.Equal(WorkItemStatus.Review, viewModel.SelectedWorkItem?.Status);
        Assert.Contains(viewModel.BoardColumns.Single(column => column.Status == WorkItemStatus.Review).Items,
            item => item.Id == "work-0001");
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("not a number")]
    [InlineData("1,500")]
    public async Task InvalidNumericParametersCannotReachTheEngine(string value)
    {
        using var viewModel = new WorkspaceViewModel(new DemoWorkspaceClient());
        await viewModel.InitializeAsync();
        viewModel.TargetSpeedText = value;
        await viewModel.SaveConfigurationCommand.ExecuteAsync();
        Assert.True(viewModel.HasError);
        Assert.Contains("invalid_number", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, viewModel.Revision);
        Assert.Equal(value, viewModel.TargetSpeedText);
    }

    [Fact]
    public async Task SavingAndSimulatingUsesTheCurrentConfigurationRevision()
    {
        using var viewModel = new WorkspaceViewModel(new DemoWorkspaceClient());
        await viewModel.InitializeAsync();
        viewModel.TargetSpeedText = "1500";
        Assert.False(viewModel.RunSimulationCommand.CanExecute(null));
        await viewModel.SaveConfigurationCommand.ExecuteAsync();
        Assert.False(viewModel.HasError);
        Assert.False(viewModel.HasUnsavedConfiguration);
        Assert.Equal(2, viewModel.SelectedConfiguration?.Revision);
        await viewModel.RunSimulationCommand.ExecuteAsync();
        Assert.False(viewModel.HasError);
        Assert.True(viewModel.LatestSimulation?.Passed);
        Assert.True(viewModel.SimulationIsCurrent);
        Assert.Equal(2, viewModel.VerifiedDriveCount);
        Assert.Equal(2, viewModel.AuditEntries.Count);
    }

    [Fact]
    public async Task SwitchingProjectsCannotLeaveACrossProjectSelection()
    {
        using var viewModel = new WorkspaceViewModel(new DemoWorkspaceClient());
        await viewModel.InitializeAsync();
        viewModel.SelectedProject = viewModel.Projects.Single(project => project.Id == SeedWorkspace.PumpProjectId);
        Assert.All(viewModel.WorkItems, item => Assert.Equal(SeedWorkspace.PumpProjectId, item.ProjectId));
        Assert.Equal(SeedWorkspace.PumpDriveId, viewModel.SelectedConfiguration?.Id);
        Assert.True(viewModel.IsReleaseReady);
    }

    [Fact]
    public async Task RefreshRetainsParameterDraftAndDiscardRestoresTheSavedValues()
    {
        using var viewModel = new WorkspaceViewModel(new DemoWorkspaceClient());
        await viewModel.InitializeAsync();
        viewModel.TargetSpeedText = "1700";
        Assert.False(viewModel.CanChangeConfigurationSelection);
        await viewModel.RefreshCommand.ExecuteAsync();
        Assert.Equal("1700", viewModel.TargetSpeedText);
        Assert.True(viewModel.HasUnsavedConfiguration);
        Assert.Equal(0, viewModel.Revision);
        viewModel.DiscardConfigurationCommand.Execute(null);
        Assert.Equal("1450", viewModel.TargetSpeedText);
        Assert.False(viewModel.HasUnsavedConfiguration);
        Assert.True(viewModel.CanChangeConfigurationSelection);
    }

    [Fact]
    public async Task ViewerCanInspectButAllMutatingCommandsAreDisabled()
    {
        using var viewModel = new WorkspaceViewModel(new DemoWorkspaceClient());
        await viewModel.InitializeAsync();
        viewModel.SelectedDemoActor = SeedWorkspace.Viewer;
        await viewModel.ChangeActorCommand.ExecuteAsync();
        viewModel.NewWorkItemTitle = "Should not be created";
        viewModel.TargetSpeedText = "1500";
        Assert.Equal(WorkspaceRole.Viewer, viewModel.CurrentActor?.Role);
        Assert.False(viewModel.CanEngineer);
        Assert.False(viewModel.CreateWorkItemCommand.CanExecute(null));
        Assert.False(viewModel.SaveConfigurationCommand.CanExecute(null));
        Assert.False(viewModel.RunSimulationCommand.CanExecute(null));
        Assert.False(viewModel.ApproveReleaseCommand.CanExecute(null));
        await viewModel.CreateWorkItemCommand.ExecuteAsync();
        Assert.Equal(0, viewModel.Revision);
    }

    [Fact]
    public async Task ReleaseFlowRequiresASecondIdentityAndRecordsBothDecisions()
    {
        using var viewModel = new WorkspaceViewModel(new DemoWorkspaceClient());
        await viewModel.InitializeAsync();
        viewModel.SelectedProject = viewModel.Projects.Single(project => project.Id == SeedWorkspace.PumpProjectId);
        await viewModel.RequestReleaseCommand.ExecuteAsync();
        Assert.False(viewModel.HasError);
        Assert.Equal(ReleaseStatus.Pending, viewModel.SelectedRelease?.Status);
        Assert.False(viewModel.ApproveReleaseCommand.CanExecute(null));
        viewModel.SelectedDemoActor = SeedWorkspace.Approver;
        await viewModel.ChangeActorCommand.ExecuteAsync();
        Assert.True(viewModel.ApproveReleaseCommand.CanExecute(null));
        await viewModel.ApproveReleaseCommand.ExecuteAsync();
        Assert.False(viewModel.HasError);
        Assert.Equal(ReleaseStatus.Approved, viewModel.SelectedRelease?.Status);
        Assert.Equal(SeedWorkspace.Approver.Id, viewModel.SelectedRelease?.ReviewedBy);
        Assert.Equal(2, viewModel.AuditEntries.Count);
    }

    [Fact]
    public async Task RejectionRequiresANoteAndPersistsIt()
    {
        using var viewModel = new WorkspaceViewModel(new DemoWorkspaceClient());
        await viewModel.InitializeAsync();
        viewModel.SelectedProject = viewModel.Projects.Single(project => project.Id == SeedWorkspace.PumpProjectId);
        await viewModel.RequestReleaseCommand.ExecuteAsync();
        viewModel.SelectedDemoActor = SeedWorkspace.Approver;
        await viewModel.ChangeActorCommand.ExecuteAsync();
        Assert.False(viewModel.RejectReleaseCommand.CanExecute(null));
        viewModel.RejectionReason = "Attach the independent current-margin review.";
        await viewModel.RejectReleaseCommand.ExecuteAsync();
        Assert.Equal(ReleaseStatus.Rejected, viewModel.SelectedRelease?.Status);
        Assert.Equal("Attach the independent current-margin review.", viewModel.SelectedRelease?.ReviewNote);
        Assert.Equal("", viewModel.RejectionReason);
    }

    [Fact]
    public async Task ServiceFailureRetainsDraftAndReleasesBusyState()
    {
        using var client = new InterceptingClient();
        using var viewModel = new WorkspaceViewModel(client);
        await viewModel.InitializeAsync();
        client.MutationOverride = (_, _) => throw new HttpRequestException("unavailable");
        viewModel.NewWorkItemTitle = "Keep this draft";
        await viewModel.CreateWorkItemCommand.ExecuteAsync();
        Assert.True(viewModel.HasError);
        Assert.Contains("Cannot reach", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("Keep this draft", viewModel.NewWorkItemTitle);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.CreateWorkItemCommand.CanExecute(null));
    }

    [Fact]
    public async Task ActivityFetchFailureDoesNotMakeASuccessfulWriteLookFailed()
    {
        using var client = new InterceptingClient();
        using var viewModel = new WorkspaceViewModel(client);
        await viewModel.InitializeAsync();
        client.FailAudit = true;
        viewModel.NewWorkItemTitle = "Persist even if the activity feed fails";
        await viewModel.CreateWorkItemCommand.ExecuteAsync();
        Assert.False(viewModel.HasError);
        Assert.Equal(1, viewModel.Revision);
        Assert.Equal("", viewModel.NewWorkItemTitle);
        Assert.Contains("Activity could not be refreshed", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConflictRefreshesRevisionPreservesParameterDraftAndNeverRetriesTheWrite()
    {
        using var client = new InterceptingClient();
        using var viewModel = new WorkspaceViewModel(client);
        await viewModel.InitializeAsync();
        await client.Inner.ExecuteAsync(new RunSimulationCommand(0, SeedWorkspace.ConveyorDriveId));
        viewModel.TargetSpeedText = "1700";
        await viewModel.SaveConfigurationCommand.ExecuteAsync();
        Assert.True(viewModel.HasError);
        Assert.Contains("not retried", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(1, client.MutationCalls);
        Assert.Equal(1, viewModel.Revision);
        Assert.Equal("1700", viewModel.TargetSpeedText);
        Assert.True(viewModel.HasUnsavedConfiguration);
        Assert.Equal(1450, viewModel.SelectedConfiguration?.TargetSpeedRpm);
    }

    [Fact]
    public async Task CancellationShowsAmbiguousWriteGuidanceAndAllowsRefresh()
    {
        using var client = new InterceptingClient();
        using var viewModel = new WorkspaceViewModel(client);
        await viewModel.InitializeAsync();
        client.MutationOverride = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return SeedWorkspace.Create(); };
        viewModel.NewWorkItemTitle = "Cancel me";
        var pending = viewModel.CreateWorkItemCommand.ExecuteAsync();
        Assert.True(viewModel.IsBusy);
        viewModel.CancelCommand.Execute(null);
        await pending;
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.HasError);
        Assert.Contains("Refresh", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("Cancel me", viewModel.NewWorkItemTitle);
        Assert.True(viewModel.RefreshCommand.CanExecute(null));
    }

    private sealed class InterceptingClient : IWorkspaceClient
    {
        public DemoWorkspaceClient Inner { get; } = new();
        public Func<WorkspaceCommand, CancellationToken, Task<WorkspaceState>>? MutationOverride { get; set; }
        public bool FailAudit { get; set; }
        public int MutationCalls { get; private set; }
        public string ModeLabel => Inner.ModeLabel;
        public string ConnectionDescription => Inner.ConnectionDescription;
        public bool AllowsActorSwitch => true;
        public void SetDemoActor(Actor actor) => Inner.SetDemoActor(actor);
        public Task<Actor> GetActorAsync(CancellationToken cancellationToken = default) => Inner.GetActorAsync(cancellationToken);
        public Task<WorkspaceState> LoadAsync(CancellationToken cancellationToken = default) => Inner.LoadAsync(cancellationToken);
        public Task<IReadOnlyList<AuditEntry>> GetAuditAsync(CancellationToken cancellationToken = default) =>
            FailAudit ? Task.FromException<IReadOnlyList<AuditEntry>>(new HttpRequestException("activity feed unavailable")) : Inner.GetAuditAsync(cancellationToken);
        public Task<WorkspaceState> ExecuteAsync(WorkspaceCommand command, CancellationToken cancellationToken = default)
        {
            MutationCalls++;
            return MutationOverride?.Invoke(command, cancellationToken) ?? Inner.ExecuteAsync(command, cancellationToken);
        }
        public void Dispose() => Inner.Dispose();
    }
}
