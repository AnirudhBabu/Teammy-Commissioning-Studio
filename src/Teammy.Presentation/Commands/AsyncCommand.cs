using System.Windows.Input;

namespace Teammy.Presentation.Commands;

/// <summary>A cancellable, non-reentrant command with an observed exception path.</summary>
public sealed class AsyncCommand(
    Func<CancellationToken, Task> execute,
    Action<Exception> onError,
    Func<bool>? canExecute = null) : ObservableObject, ICommand, IDisposable
{
    private int _running;
    private bool _disposed;
    private CancellationTokenSource? _cancellation;

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_disposed && !IsRunning && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync();

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null) || Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        OnPropertyChanged(nameof(IsRunning));
        NotifyCanExecuteChanged();
        try
        {
            await execute(cancellation.Token);
        }
        catch (Exception error)
        {
            // ICommand.Execute is async void, so every exception must be observed here.
            onError(error);
        }
        finally
        {
            _cancellation = null;
            Interlocked.Exchange(ref _running, 0);
            OnPropertyChanged(nameof(IsRunning));
            NotifyCanExecuteChanged();
        }
    }

    public void Cancel() => _cancellation?.Cancel();

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _disposed = true;
        Cancel();
        NotifyCanExecuteChanged();
    }
}
