using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Teammy.Application;
using Teammy.Domain;
using Teammy.Presentation;
using Teammy.Presentation.Clients;
using Teammy.Presentation.ViewModels;

namespace Teammy.Desktop;

public partial class App : System.Windows.Application
{
    private WorkspaceViewModel? _viewModel;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? smokePath = null;
        BindingErrorListener? bindingErrors = null;
        try
        {
            var options = LaunchOptions.Parse(e.Args);
            smokePath = options.SmokeTestPath;
            if (smokePath is not null)
            {
                bindingErrors = new BindingErrorListener();
                PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            }
            IWorkspaceClient client;
            if (options.ApiAddress is null)
            {
                client = new DemoWorkspaceClient();
            }
            else
            {
                var token = Environment.GetEnvironmentVariable("TEAMMY_API_TOKEN");
                if (!options.DemoAuthentication && string.IsNullOrWhiteSpace(token))
                {
                    throw new InvalidOperationException("Set TEAMMY_API_TOKEN to a current bearer access token before using --api. The token is kept only in this process and is never saved by Teammy.");
                }

                var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                client = new HttpWorkspaceClient(http, options.ApiAddress, token, options.DemoAuthentication);
            }

            _viewModel = new WorkspaceViewModel(client);
            var window = new MainWindow(_viewModel);
            MainWindow = window;
            window.Show();
            await _viewModel.InitializeAsync();

            if (smokePath is not null)
            {
                if (_viewModel.HasError || !_viewModel.HasWorkspace)
                {
                    throw new InvalidOperationException("Desktop startup smoke test failed: " + _viewModel.ErrorMessage);
                }

                await _viewModel.RunSimulationCommand.ExecuteAsync();
                if (_viewModel.HasError || _viewModel.LatestSimulation is null || _viewModel.AuditEntries.Count == 0)
                {
                    throw new InvalidOperationException("Desktop simulation smoke test failed: " + _viewModel.ErrorMessage);
                }

                // Exercise the engineering-to-independent-review path before checking every visual surface.
                foreach (var workItemId in _viewModel.WorkItems
                    .Where(item => item.Status != WorkItemStatus.Done && item.Priority is WorkItemPriority.High or WorkItemPriority.Critical)
                    .Select(item => item.Id).ToArray())
                {
                    _viewModel.SelectedWorkItem = _viewModel.WorkItems.Single(item => item.Id == workItemId);
                    for (var step = 0; step < 4 && _viewModel.SelectedWorkItem.Status != WorkItemStatus.Done; step++)
                    {
                        _viewModel.TargetWorkItemStatus = _viewModel.SelectedWorkItem.Status switch
                        {
                            WorkItemStatus.Backlog or WorkItemStatus.Blocked => WorkItemStatus.InProgress,
                            WorkItemStatus.InProgress => WorkItemStatus.Review,
                            WorkItemStatus.Review => WorkItemStatus.Done,
                            _ => throw new InvalidOperationException("Unexpected work-item state during desktop smoke test.")
                        };
                        await _viewModel.TransitionWorkItemCommand.ExecuteAsync();
                        AssertSmokeSucceeded(_viewModel);
                    }
                }

                await _viewModel.RequestReleaseCommand.ExecuteAsync();
                AssertSmokeSucceeded(_viewModel);
                _viewModel.SelectedDemoActor = SeedWorkspace.Approver;
                await _viewModel.ChangeActorCommand.ExecuteAsync();
                await _viewModel.ApproveReleaseCommand.ExecuteAsync();
                AssertSmokeSucceeded(_viewModel);
                if (_viewModel.SelectedRelease?.Status != ReleaseStatus.Approved)
                {
                    throw new InvalidOperationException("Desktop smoke test did not produce an independently approved release.");
                }

                _viewModel.SelectedDemoActor = SeedWorkspace.Engineer;
                await _viewModel.ChangeActorCommand.ExecuteAsync();
                // Capture actual WPF surfaces so CI provides visible evidence, not a static mockup.
                await RenderScreenshotAsync(window, smokePath);
                foreach (var (index, suffix) in new[] { (1, "workboard"), (2, "drive"), (3, "releases"), (4, "activity") })
                {
                    _viewModel.SelectedTabIndex = index;
                    var tabPath = Path.Combine(Path.GetDirectoryName(smokePath) ?? ".",
                        Path.GetFileNameWithoutExtension(smokePath) + $"-{suffix}.png");
                    await RenderScreenshotAsync(window, tabPath);
                }
                if (bindingErrors?.HasErrors == true)
                {
                    throw new InvalidOperationException("WPF binding errors were detected during the smoke test: " + bindingErrors.Messages);
                }

                Shutdown(0);
            }
        }
        catch (Exception error)
        {
            var message = error is ArgumentException or InvalidOperationException ? error.Message :
                "Teammy could not start. Verify the .NET desktop runtime and launch options.";
            if (smokePath is not null || e.Args.Contains("--smoke-test", StringComparer.Ordinal))
            {
                Console.Error.WriteLine(message);
                if (smokePath is not null)
                {
                    var errorPath = Path.GetFullPath(smokePath + ".error.txt");
                    Directory.CreateDirectory(Path.GetDirectoryName(errorPath)!);
                    await File.WriteAllTextAsync(errorPath, error.ToString());
                }
            }
            else
            {
                MessageBox.Show(message, "Teammy · Startup needs attention", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            Shutdown(1);
        }
        finally
        {
            if (bindingErrors is not null)
            {
                PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingErrors);
                bindingErrors.Dispose();
            }
        }
    }

    private static async Task RenderScreenshotAsync(Window window, string path)
    {
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        if (width < 100 || height < 100)
        {
            throw new InvalidOperationException("The WPF window did not produce a usable layout.");
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using var stream = File.Create(fullPath);
        encoder.Save(stream);
    }

    private static void AssertSmokeSucceeded(WorkspaceViewModel viewModel)
    {
        if (viewModel.HasError)
        {
            throw new InvalidOperationException("Desktop workflow smoke test failed: " + viewModel.ErrorMessage);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        base.OnExit(e);
    }

    private sealed class BindingErrorListener : TraceListener
    {
        private readonly StringBuilder _messages = new();
        public bool HasErrors => _messages.Length > 0;
        public string Messages => _messages.ToString();
        public override void Write(string? message) => _messages.Append(message);
        public override void WriteLine(string? message) => _messages.AppendLine(message);
    }
}
