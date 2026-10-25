using Teammy.Presentation.Commands;
using Xunit;

namespace Teammy.Presentation.Tests;

public sealed class AsyncCommandTests
{
    [Fact]
    public async Task ASecondExecutionCannotStartUntilTheFirstCompletes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        using var command = new AsyncCommand(async _ => { executions++; await gate.Task; }, _ => { });
        var first = command.ExecuteAsync();
        await command.ExecuteAsync();
        Assert.True(command.IsRunning);
        Assert.False(command.CanExecute(null));
        Assert.Equal(1, executions);
        gate.SetResult();
        await first;
        Assert.False(command.IsRunning);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task CancellationIsObservedAndTheCommandCanBeUsedAgain()
    {
        Exception? observed = null;
        using var command = new AsyncCommand(token => Task.Delay(Timeout.Infinite, token), error => observed = error);
        var pending = command.ExecuteAsync();
        command.Cancel();
        await pending;
        Assert.IsAssignableFrom<OperationCanceledException>(observed);
        Assert.False(command.IsRunning);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task FaultsAreDeliveredToTheErrorPresenterWithoutEscapingAsyncVoid()
    {
        Exception? observed = null;
        var expected = new InvalidOperationException("service unavailable");
        using var command = new AsyncCommand(_ => Task.FromException(expected), error => observed = error);
        await command.ExecuteAsync();
        Assert.Same(expected, observed);
        Assert.False(command.IsRunning);
    }

    [Fact]
    public async Task DisabledCommandDoesNotRun()
    {
        var called = false;
        using var command = new AsyncCommand(_ => { called = true; return Task.CompletedTask; }, _ => { }, () => false);
        await command.ExecuteAsync();
        Assert.False(called);
    }

    [Fact]
    public async Task DisposalCancelsOutstandingWorkAndPreventsFutureExecution()
    {
        var executions = 0;
        using var command = new AsyncCommand(async token => { executions++; await Task.Delay(Timeout.Infinite, token); }, _ => { });
        var pending = command.ExecuteAsync();
        command.Dispose();
        await pending;
        await command.ExecuteAsync();
        Assert.Equal(1, executions);
        Assert.False(command.CanExecute(null));
    }
}
