using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Teammy.Application;
using Teammy.Domain;
using Teammy.Presentation.Clients;
using Teammy.Presentation.Commands;

namespace Teammy.Presentation.ViewModels;

/// <summary>Presentation state only. Authorization, validation, and lifecycle rules stay in the engine/API.</summary>
public sealed class WorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly IWorkspaceClient _client;
    private readonly AsyncCommand[] _commands;
    private WorkspaceState? _state;
    private Actor? _currentActor;
    private Actor _selectedDemoActor = SeedWorkspace.Engineer;
    private EngineeringProject? _selectedProject;
    private DriveConfiguration? _selectedConfiguration;
    private WorkItem? _selectedWorkItem;
    private ReleaseCandidate? _selectedRelease;
    private Member? _selectedAssignee;
    private bool _isBusy;
    private bool _loadingEditor;
    private string _statusMessage = "Open a project, tune a drive, and prove it is ready for release.";
    private string _errorMessage = "";
    private string _newWorkItemTitle = "";
    private string _newWorkItemDescription = "";
    private WorkItemPriority _newWorkItemPriority = WorkItemPriority.Normal;
    private WorkItemStatus _targetWorkItemStatus = WorkItemStatus.InProgress;
    private DateTime? _newDueDate = DateTime.Today.AddDays(7);
    private string _targetSpeedText = "";
    private string _accelerationText = "";
    private string _decelerationText = "";
    private string _currentLimitText = "";
    private string _loadText = "";
    private string _releaseNotes = "Configuration and commissioning checks reviewed.";
    private string _rejectionReason = "";
    private IReadOnlyList<AuditEntry> _auditEntries = [];
    private int _selectedTabIndex;

    public WorkspaceViewModel(IWorkspaceClient client)
    {
        _client = client;
        RefreshCommand = new AsyncCommand(InitializeAsync, PresentError, () => !IsBusy);
        CreateWorkItemCommand = new AsyncCommand(CreateWorkItemAsync, PresentError,
            () => CanEngineer && !string.IsNullOrWhiteSpace(NewWorkItemTitle) && SelectedAssignee is not null);
        TransitionWorkItemCommand = new AsyncCommand(TransitionWorkItemAsync, PresentError,
            () => CanEngineer && SelectedWorkItem is not null && TargetWorkItemStatus != SelectedWorkItem.Status);
        SaveConfigurationCommand = new AsyncCommand(SaveConfigurationAsync, PresentError,
            () => CanEngineer && SelectedConfiguration is not null && HasUnsavedConfiguration);
        RunSimulationCommand = new AsyncCommand(RunSimulationAsync, PresentError,
            () => CanEngineer && SelectedConfiguration is not null && !HasUnsavedConfiguration);
        RequestReleaseCommand = new AsyncCommand(RequestReleaseAsync, PresentError,
            () => CanEngineer && SelectedProject is not null && IsReleaseReady && !HasPendingRelease);
        ApproveReleaseCommand = new AsyncCommand(ApproveReleaseAsync, PresentError,
            () => CanReviewSelectedRelease);
        RejectReleaseCommand = new AsyncCommand(RejectReleaseAsync, PresentError,
            () => CanReviewSelectedRelease && !string.IsNullOrWhiteSpace(RejectionReason));
        ChangeActorCommand = new AsyncCommand(ChangeActorAsync, PresentError, () => AllowsActorSwitch && !IsBusy);
        _commands = [RefreshCommand, CreateWorkItemCommand, TransitionWorkItemCommand,
            SaveConfigurationCommand, RunSimulationCommand, RequestReleaseCommand,
            ApproveReleaseCommand, RejectReleaseCommand, ChangeActorCommand];
        SelectWorkItemCommand = new RelayCommand(value => SelectedWorkItem = value as WorkItem, _ => !IsBusy);
        CancelCommand = new RelayCommand(_ => CancelOperations(), _ => IsBusy);
        DismissErrorCommand = new RelayCommand(_ => ErrorMessage = "");
        DiscardConfigurationCommand = new RelayCommand(_ =>
        {
            LoadConfigurationEditor();
            StatusMessage = "Unsaved parameters discarded. The saved configuration is unchanged.";
        }, _ => !IsBusy && HasUnsavedConfiguration);
        NavigateCommand = new RelayCommand(value =>
        {
            if (int.TryParse(value?.ToString(), out var index))
            {
                SelectedTabIndex = index;
            }
        });
    }

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand CreateWorkItemCommand { get; }
    public AsyncCommand TransitionWorkItemCommand { get; }
    public AsyncCommand SaveConfigurationCommand { get; }
    public AsyncCommand RunSimulationCommand { get; }
    public AsyncCommand RequestReleaseCommand { get; }
    public AsyncCommand ApproveReleaseCommand { get; }
    public AsyncCommand RejectReleaseCommand { get; }
    public AsyncCommand ChangeActorCommand { get; }
    public RelayCommand SelectWorkItemCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand DismissErrorCommand { get; }
    public RelayCommand DiscardConfigurationCommand { get; }
    public RelayCommand NavigateCommand { get; }
    public IReadOnlyList<Actor> DemoActors { get; } = [SeedWorkspace.Engineer, SeedWorkspace.Approver, SeedWorkspace.Viewer];
    public IReadOnlyList<WorkItemPriority> Priorities { get; } = Enum.GetValues<WorkItemPriority>();
    public IReadOnlyList<WorkItemStatus> WorkItemStatuses => SelectedWorkItem is null ? [] :
        WorkspaceEngine.AllowedTransitions(SelectedWorkItem.Status).ToArray();
    public string ModeLabel => _client.ModeLabel;
    public string ConnectionDescription => _client.ConnectionDescription;
    public bool AllowsActorSwitch => _client.AllowsActorSwitch;
    public bool HasWorkspace => _state is not null;
    public int Revision => _state?.Revision ?? 0;
    public string RevisionLabel => $"REV {Revision:D4}";
    public Actor? CurrentActor => _currentActor;
    public string ActorDisplay => _currentActor is null ? "Connecting…" : $"{_currentActor.DisplayName} · {_currentActor.Role}";
    public bool CanEngineer => !IsBusy && _currentActor?.Role == WorkspaceRole.Engineer;
    public bool CanApprove => !IsBusy && _currentActor?.Role == WorkspaceRole.Approver;
    public string RoleHint => _currentActor?.Role switch
    {
        WorkspaceRole.Engineer => "Engineer · configure, simulate, and request a release",
        WorkspaceRole.Approver => "Approver · independently approve or reject a release",
        WorkspaceRole.Viewer => "Viewer · inspect the workspace and activity",
        _ => "Connect to load your permissions"
    };

    public IReadOnlyList<EngineeringProject> Projects => _state?.Projects.ToArray() ?? [];
    public IReadOnlyList<WorkItem> WorkItems => _state?.WorkItems.Where(item => item.ProjectId == SelectedProject?.Id).ToArray() ?? [];
    public IReadOnlyList<DriveConfiguration> Configurations => _state?.Configurations.Where(config => config.ProjectId == SelectedProject?.Id).ToArray() ?? [];
    public IReadOnlyList<ReleaseCandidate> Releases => _state?.Releases.Where(release => release.ProjectId == SelectedProject?.Id)
        .OrderByDescending(release => release.RequestedAt).ToArray() ?? [];
    public IReadOnlyList<Member> TeamMembers
    {
        get
        {
            var team = _state?.Teams.FirstOrDefault(team => team.Id == SelectedProject?.TeamId);
            return team is null ? [] : _state!.Members.Where(member => team.MemberIds.Contains(member.Id)).ToArray();
        }
    }

    public IReadOnlyList<BoardColumn> BoardColumns =>
    [
        new("Backlog", WorkItemStatus.Backlog, WorkItems.Where(item => item.Status == WorkItemStatus.Backlog).ToArray()),
        new("In progress", WorkItemStatus.InProgress, WorkItems.Where(item => item.Status == WorkItemStatus.InProgress).ToArray()),
        new("Blocked", WorkItemStatus.Blocked, WorkItems.Where(item => item.Status == WorkItemStatus.Blocked).ToArray()),
        new("In review", WorkItemStatus.Review, WorkItems.Where(item => item.Status == WorkItemStatus.Review).ToArray()),
        new("Done", WorkItemStatus.Done, WorkItems.Where(item => item.Status == WorkItemStatus.Done).ToArray())
    ];

    public int OpenWorkItemCount => WorkItems.Count(item => item.Status != WorkItemStatus.Done);
    public int BlockingWorkItemCount => WorkItems.Count(item => item.Status != WorkItemStatus.Done &&
        item.Priority is WorkItemPriority.High or WorkItemPriority.Critical);
    public int DoneWorkItemCount => WorkItems.Count(item => item.Status == WorkItemStatus.Done);
    public int DriveCount => Configurations.Count;
    public int VerifiedDriveCount => Configurations.Count(config => _state!.SimulationRuns
        .Where(run => run.ConfigurationId == config.Id && run.ConfigurationRevision == config.Revision)
        .OrderByDescending(run => run.CompletedAt).FirstOrDefault()?.Passed == true);
    public double CompletionPercentage => WorkItems.Count == 0 ? 0 : 100d * DoneWorkItemCount / WorkItems.Count;
    public string CompletionLabel => $"{DoneWorkItemCount} / {WorkItems.Count} work items complete";
    public string VerificationLabel => $"{VerifiedDriveCount} / {DriveCount} configurations verified";
    public string ProjectTeamName => _state?.Teams.FirstOrDefault(team => team.Id == SelectedProject?.TeamId)?.Name ?? "Engineering team";
    public string WorkspaceTitle => SelectedProject?.Name ?? "Commissioning workspace";
    public string WorkspaceDescription => SelectedProject?.Description ?? "Loading your engineering workspace…";
    public string WorkspaceLocation => SelectedProject?.Location ?? "";
    public bool HasPendingRelease => Releases.Any(release => release.Status == ReleaseStatus.Pending);
    public ReleaseReadiness? Readiness => _state is not null && SelectedProject is not null
        ? WorkspaceEngine.EvaluateReleaseReadiness(_state, SelectedProject.Id) : null;
    public IReadOnlyList<ReadinessCheck> ReadinessChecks => Readiness?.Checks.ToArray() ?? [];
    public bool IsReleaseReady => Readiness?.IsReady == true;
    public string ReadinessLabel => IsReleaseReady ? "Ready for review" : "Gates outstanding";
    public string ReadinessDescription => IsReleaseReady
        ? "The current project passes all release gates. Request an independent review."
        : "Complete blocking work and pass simulation for every current drive configuration.";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(CanEngineer));
                OnPropertyChanged(nameof(CanApprove));
                OnPropertyChanged(nameof(CanReviewSelectedRelease));
                OnPropertyChanged(nameof(CanChangeConfigurationSelection));
                RefreshCommands();
                CancelCommand.NotifyCanExecuteChanged();
                SelectWorkItemCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsIdle => !IsBusy;
    public bool CanChangeConfigurationSelection => !IsBusy && !HasUnsavedConfiguration;
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public int SelectedTabIndex { get => _selectedTabIndex; set => SetProperty(ref _selectedTabIndex, value); }
    public Actor SelectedDemoActor { get => _selectedDemoActor; set => SetProperty(ref _selectedDemoActor, value); }

    public EngineeringProject? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (SetProperty(ref _selectedProject, value))
            {
                RefreshProjectState();
            }
        }
    }

    public DriveConfiguration? SelectedConfiguration
    {
        get => _selectedConfiguration;
        set
        {
            if (SetProperty(ref _selectedConfiguration, value))
            {
                LoadConfigurationEditor();
                RefreshSimulationProperties();
                RefreshCommands();
            }
        }
    }

    public WorkItem? SelectedWorkItem
    {
        get => _selectedWorkItem;
        set
        {
            if (SetProperty(ref _selectedWorkItem, value))
            {
                TargetWorkItemStatus = value?.Status switch
                {
                    WorkItemStatus.Backlog => WorkItemStatus.InProgress,
                    WorkItemStatus.InProgress => WorkItemStatus.Review,
                    WorkItemStatus.Blocked => WorkItemStatus.InProgress,
                    WorkItemStatus.Review => WorkItemStatus.Done,
                    _ => WorkItemStatus.InProgress
                };
                OnPropertyChanged(nameof(SelectedWorkItemAssignee));
                OnPropertyChanged(nameof(WorkItemStatuses));
                RefreshCommands();
            }
        }
    }

    public string SelectedWorkItemAssignee => _state?.Members.FirstOrDefault(member => member.Id == SelectedWorkItem?.AssigneeId)?.DisplayName ?? "";
    public ReleaseCandidate? SelectedRelease
    {
        get => _selectedRelease;
        set
        {
            if (SetProperty(ref _selectedRelease, value))
            {
                OnPropertyChanged(nameof(CanReviewSelectedRelease));
                OnPropertyChanged(nameof(ReviewHint));
                OnPropertyChanged(nameof(ReleaseEvidenceLabel));
                RefreshCommands();
            }
        }
    }

    public bool CanReviewSelectedRelease => CanApprove && SelectedRelease?.Status == ReleaseStatus.Pending &&
        SelectedRelease.RequestedBy != _currentActor?.Id && !SelectedRelease.ConfigurationAuthors.Contains(_currentActor!.Id);
    public string ReviewHint => SelectedRelease is null ? "Select a release candidate to inspect its review." :
        SelectedRelease.Status != ReleaseStatus.Pending ? $"This candidate is {SelectedRelease.Status.ToString().ToLowerInvariant()}." :
        _currentActor?.Role != WorkspaceRole.Approver ? "An independent Approver must review this candidate." :
        CanReviewSelectedRelease ? "Your approval records the current configuration fingerprint." :
        "The requester and configuration authors cannot approve their own release.";
    public string ReleaseEvidenceLabel => SelectedRelease?.Status == ReleaseStatus.Approved
        ? "Historical approval · applies only to this candidate fingerprint"
        : "Candidate evidence · readiness is checked again when a reviewer decides";

    public Member? SelectedAssignee { get => _selectedAssignee; set { if (SetProperty(ref _selectedAssignee, value)) RefreshCommands(); } }
    public string NewWorkItemTitle { get => _newWorkItemTitle; set { if (SetProperty(ref _newWorkItemTitle, value)) RefreshCommands(); } }
    public string NewWorkItemDescription { get => _newWorkItemDescription; set => SetProperty(ref _newWorkItemDescription, value); }
    public WorkItemPriority NewWorkItemPriority { get => _newWorkItemPriority; set => SetProperty(ref _newWorkItemPriority, value); }
    public WorkItemStatus TargetWorkItemStatus { get => _targetWorkItemStatus; set { if (SetProperty(ref _targetWorkItemStatus, value)) RefreshCommands(); } }
    public DateTime? NewDueDate { get => _newDueDate; set => SetProperty(ref _newDueDate, value); }
    public string ReleaseNotes { get => _releaseNotes; set => SetProperty(ref _releaseNotes, value); }
    public string RejectionReason { get => _rejectionReason; set { if (SetProperty(ref _rejectionReason, value)) RefreshCommands(); } }
    public string TargetSpeedText { get => _targetSpeedText; set { if (SetProperty(ref _targetSpeedText, value)) EditorChanged(); } }
    public string AccelerationText { get => _accelerationText; set { if (SetProperty(ref _accelerationText, value)) EditorChanged(); } }
    public string DecelerationText { get => _decelerationText; set { if (SetProperty(ref _decelerationText, value)) EditorChanged(); } }
    public string CurrentLimitText { get => _currentLimitText; set { if (SetProperty(ref _currentLimitText, value)) EditorChanged(); } }
    public string LoadText { get => _loadText; set { if (SetProperty(ref _loadText, value)) EditorChanged(); } }
    public bool HasUnsavedConfiguration => SelectedConfiguration is not null &&
        (TargetSpeedText != Format(SelectedConfiguration.TargetSpeedRpm) ||
         AccelerationText != Format(SelectedConfiguration.AccelerationSeconds) ||
         DecelerationText != Format(SelectedConfiguration.DecelerationSeconds) ||
         CurrentLimitText != Format(SelectedConfiguration.CurrentLimitAmps) ||
         LoadText != Format(SelectedConfiguration.LoadPercent));
    public string ConfigurationHint => HasUnsavedConfiguration ? "Unsaved parameters · save before simulation" : "Saved parameters · ready to simulate";
    public DriveProfile? SelectedProfile => _state?.DriveProfiles.FirstOrDefault(profile => profile.Id == SelectedConfiguration?.ProfileId);
    public SimulationRun? LatestSimulation => _state?.SimulationRuns.Where(run => run.ConfigurationId == SelectedConfiguration?.Id)
        .OrderByDescending(run => run.CompletedAt).FirstOrDefault();
    public bool SimulationIsCurrent => LatestSimulation is not null && LatestSimulation.ConfigurationRevision == SelectedConfiguration?.Revision;
    public string SimulationLabel => LatestSimulation is null ? "Awaiting simulation" : !SimulationIsCurrent ? "Results are stale" :
        LatestSimulation.Passed ? "Simulation passed" : "Simulation failed";
    public string SimulationDetail => LatestSimulation is null ? "Run a deterministic check against the saved parameters." :
        $"Configuration revision {LatestSimulation.ConfigurationRevision} · {LatestSimulation.CompletedAt:HH:mm:ss} UTC";
    public IReadOnlyList<SimulationCheck> SimulationChecks => LatestSimulation?.Checks.ToArray() ?? [];
    public IReadOnlyList<TelemetrySample> Telemetry => LatestSimulation?.Samples.ToArray() ?? [];
    public string PeakSpeed => LatestSimulation is null ? "—" : $"{LatestSimulation.Samples.Max(sample => sample.SpeedRpm):0} rpm";
    public string PeakCurrent => LatestSimulation is null ? "—" : $"{LatestSimulation.Samples.Max(sample => sample.CurrentAmps):0.0} A";
    public string PeakTemperature => LatestSimulation is null ? "—" : $"{LatestSimulation.Samples.Max(sample => sample.TemperatureCelsius):0.0} °C";
    public IReadOnlyList<AuditEntry> AuditEntries { get => _auditEntries; private set => SetProperty(ref _auditEntries, value); }
    public bool HasAuditEntries => AuditEntries.Count > 0;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await RunOperationAsync("Refreshing the workspace…", async token =>
        {
            var preserveDraft = HasUnsavedConfiguration;
            // Fetch identity before enabling any editing affordances.
            _currentActor = null;
            RefreshIdentityProperties();
            _currentActor = await _client.GetActorAsync(token);
            var state = await _client.LoadAsync(token);
            ApplyState(state, preserveConfigurationDraft: preserveDraft);
            RefreshIdentityProperties();
            StatusMessage = preserveDraft ? "Workspace refreshed. Your unsaved parameter draft is retained." :
                "Workspace refreshed. All devices in this project are simulated.";
            await RefreshAuditAsync(token);
        }, cancellationToken);
    }

    private Task ChangeActorAsync(CancellationToken cancellationToken) => RunOperationAsync("Switching demonstration identity…", async token =>
    {
        _client.SetDemoActor(SelectedDemoActor);
        _currentActor = await _client.GetActorAsync(token);
        RefreshIdentityProperties();
        StatusMessage = $"Demonstration identity: {_currentActor.DisplayName} ({_currentActor.Role}).";
    }, cancellationToken);

    private Task CreateWorkItemAsync(CancellationToken token) => ExecuteMutationAsync(
        () => new CreateWorkItemCommand(Revision, SelectedProject!.Id, NewWorkItemTitle.Trim(),
            NewWorkItemDescription.Trim(), SelectedAssignee!.Id, NewWorkItemPriority,
            NewDueDate is null ? null : DateOnly.FromDateTime(NewDueDate.Value)),
        "Work item created.", token, () => { NewWorkItemTitle = ""; NewWorkItemDescription = ""; });

    private Task TransitionWorkItemAsync(CancellationToken token) => ExecuteMutationAsync(
        () => new TransitionWorkItemCommand(Revision, SelectedWorkItem!.Id, TargetWorkItemStatus),
        "Work item moved. Release gates have been re-evaluated.", token);

    private Task SaveConfigurationAsync(CancellationToken token) => ExecuteMutationAsync(
        () => new UpdateDriveConfigurationCommand(Revision, SelectedConfiguration!.Id,
            ReadNumber(TargetSpeedText, "Target speed"), ReadNumber(AccelerationText, "Acceleration time"),
            ReadNumber(DecelerationText, "Deceleration time"), ReadNumber(CurrentLimitText, "Current limit"),
            ReadNumber(LoadText, "Load")),
        "Parameters saved. Run simulation again to verify this configuration revision.", token);

    private Task RunSimulationAsync(CancellationToken token) => ExecuteMutationAsync(
        () => new RunSimulationCommand(Revision, SelectedConfiguration!.Id),
        "Simulation complete. Inspect the checks and telemetry before requesting a release.", token);

    private Task RequestReleaseAsync(CancellationToken token) => ExecuteMutationAsync(
        () => new RequestReleaseCommand(Revision, SelectedProject!.Id, ReleaseNotes.Trim()),
        "Release requested. An independent approver must review this candidate.", token);

    private Task ApproveReleaseAsync(CancellationToken token) => ExecuteMutationAsync(
        () => new ApproveReleaseCommand(Revision, SelectedRelease!.Id),
        "Release approved. The review and configuration fingerprint are recorded in the audit trail.", token);

    private Task RejectReleaseAsync(CancellationToken token) => ExecuteMutationAsync(
        () => new RejectReleaseCommand(Revision, SelectedRelease!.Id, RejectionReason.Trim()),
        "Release rejected with your review note.", token, () => RejectionReason = "");

    private Task ExecuteMutationAsync(Func<WorkspaceCommand> createCommand, string successMessage,
        CancellationToken cancellationToken, Action? afterSuccess = null) => RunOperationAsync("Applying change…", async token =>
    {
        try
        {
            var state = await _client.ExecuteAsync(createCommand(), token);
            ApplyState(state);
            afterSuccess?.Invoke();
            StatusMessage = successMessage;
            // A failed activity read cannot turn a committed operation into an apparent failure.
            await RefreshAuditAsync(token);
        }
        catch (WorkspaceException error) when (error.Kind == WorkspaceErrorKind.Conflict)
        {
            try
            {
                ApplyState(await _client.LoadAsync(token), preserveConfigurationDraft: true);
            }
            catch (Exception reloadError) when (reloadError is HttpRequestException or OperationCanceledException or WorkspaceException or JsonException or InvalidOperationException)
            {
                StatusMessage = "The change was not applied. Refresh when the service is reachable.";
            }

            throw new WorkspaceException(WorkspaceErrorKind.Conflict, error.Code,
                error.Message + " The action was not retried. Review the latest state and your draft before trying again.");
        }
    }, cancellationToken);

    private async Task RunOperationAsync(string status, Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = "";
        StatusMessage = status;
        try
        {
            await operation(cancellationToken);
        }
        catch (Exception error)
        {
            PresentError(error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAuditAsync(CancellationToken cancellationToken)
    {
        try
        {
            AuditEntries = await _client.GetAuditAsync(cancellationToken);
            OnPropertyChanged(nameof(HasAuditEntries));
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or WorkspaceException or JsonException or InvalidOperationException)
        {
            StatusMessage += " Activity could not be refreshed; the workspace change is retained.";
        }
    }

    private void ApplyState(WorkspaceState state, bool preserveConfigurationDraft = false)
    {
        var projectId = SelectedProject?.Id;
        var configurationId = SelectedConfiguration?.Id;
        var workItemId = SelectedWorkItem?.Id;
        var releaseId = SelectedRelease?.Id;
        var draft = (TargetSpeedText, AccelerationText, DecelerationText, CurrentLimitText, LoadText);
        _state = state;
        _selectedProject = state.Projects.FirstOrDefault(project => project.Id == projectId) ?? state.Projects.FirstOrDefault();
        OnPropertyChanged(nameof(Projects));
        OnPropertyChanged(nameof(SelectedProject));
        OnPropertyChanged(nameof(HasWorkspace));
        OnPropertyChanged(nameof(Revision));
        OnPropertyChanged(nameof(RevisionLabel));
        RefreshProjectState(configurationId, workItemId, releaseId);
        if (preserveConfigurationDraft && configurationId == SelectedConfiguration?.Id)
        {
            (TargetSpeedText, AccelerationText, DecelerationText, CurrentLimitText, LoadText) = draft;
        }
    }

    private void RefreshProjectState(string? configurationId = null, string? workItemId = null, string? releaseId = null)
    {
        SelectedConfiguration = Configurations.FirstOrDefault(config => config.Id == configurationId) ?? Configurations.FirstOrDefault();
        SelectedWorkItem = WorkItems.FirstOrDefault(item => item.Id == workItemId) ?? WorkItems.FirstOrDefault();
        SelectedRelease = Releases.FirstOrDefault(release => release.Id == releaseId) ?? Releases.FirstOrDefault();
        SelectedAssignee = TeamMembers.FirstOrDefault(member => member.Id == _currentActor?.Id) ?? TeamMembers.FirstOrDefault();
        foreach (var property in new[]
        {
            nameof(WorkItems), nameof(Configurations), nameof(Releases), nameof(TeamMembers), nameof(BoardColumns),
            nameof(OpenWorkItemCount), nameof(BlockingWorkItemCount), nameof(DoneWorkItemCount), nameof(DriveCount),
            nameof(VerifiedDriveCount), nameof(CompletionPercentage), nameof(CompletionLabel), nameof(VerificationLabel),
            nameof(ProjectTeamName), nameof(WorkspaceTitle), nameof(WorkspaceDescription), nameof(WorkspaceLocation),
            nameof(HasPendingRelease), nameof(Readiness), nameof(ReadinessChecks), nameof(IsReleaseReady),
            nameof(ReadinessLabel), nameof(ReadinessDescription), nameof(ReviewHint), nameof(ReleaseEvidenceLabel)
        })
        {
            OnPropertyChanged(property);
        }

        RefreshSimulationProperties();
        RefreshCommands();
    }

    private void LoadConfigurationEditor()
    {
        _loadingEditor = true;
        TargetSpeedText = SelectedConfiguration is null ? "" : Format(SelectedConfiguration.TargetSpeedRpm);
        AccelerationText = SelectedConfiguration is null ? "" : Format(SelectedConfiguration.AccelerationSeconds);
        DecelerationText = SelectedConfiguration is null ? "" : Format(SelectedConfiguration.DecelerationSeconds);
        CurrentLimitText = SelectedConfiguration is null ? "" : Format(SelectedConfiguration.CurrentLimitAmps);
        LoadText = SelectedConfiguration is null ? "" : Format(SelectedConfiguration.LoadPercent);
        _loadingEditor = false;
        EditorChanged();
    }

    private void EditorChanged()
    {
        if (_loadingEditor)
        {
            return;
        }

        OnPropertyChanged(nameof(HasUnsavedConfiguration));
        OnPropertyChanged(nameof(ConfigurationHint));
        OnPropertyChanged(nameof(CanChangeConfigurationSelection));
        RefreshCommands();
    }

    private void RefreshSimulationProperties()
    {
        foreach (var property in new[]
        {
            nameof(SelectedProfile), nameof(LatestSimulation), nameof(SimulationIsCurrent), nameof(SimulationLabel),
            nameof(SimulationDetail), nameof(SimulationChecks), nameof(Telemetry), nameof(PeakSpeed), nameof(PeakCurrent), nameof(PeakTemperature)
        })
        {
            OnPropertyChanged(property);
        }
    }

    private void RefreshIdentityProperties()
    {
        foreach (var property in new[] { nameof(CurrentActor), nameof(ActorDisplay), nameof(RoleHint), nameof(CanEngineer),
            nameof(CanApprove), nameof(CanReviewSelectedRelease), nameof(ReviewHint) })
        {
            OnPropertyChanged(property);
        }

        RefreshCommands();
    }

    private void PresentError(Exception error)
    {
        ErrorMessage = error switch
        {
            WorkspaceException workspaceError => workspaceError.Message + $" [{workspaceError.Code}]",
            OperationCanceledException => "Operation canceled or timed out. Refresh to confirm the service state before retrying a write.",
            HttpRequestException => "Cannot reach the service. Check your connection and API URL, then refresh. A write may have reached the server.",
            _ => "The operation could not be completed. Refresh the workspace and try again."
        };
        StatusMessage = "Action needs attention.";
    }

    private void RefreshCommands()
    {
        if (_commands is null)
        {
            return;
        }

        foreach (var command in _commands)
        {
            command.NotifyCanExecuteChanged();
        }

        DiscardConfigurationCommand?.NotifyCanExecuteChanged();
    }

    private void CancelOperations()
    {
        foreach (var command in _commands)
        {
            command.Cancel();
        }
    }

    private static double ReadNumber(string value, string label)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
        {
            throw new WorkspaceException(WorkspaceErrorKind.Validation, "invalid_number",
                $"{label} must be a finite number. Use a dot for the decimal separator.");
        }

        return number;
    }

    private static string Format(double number) => number.ToString("G", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        foreach (var command in _commands)
        {
            command.Dispose();
        }

        _client.Dispose();
    }
}
