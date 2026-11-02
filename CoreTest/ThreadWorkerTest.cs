using Microsoft.Extensions.Time.Testing;

namespace AVCoders.Core.Tests;

public class ThreadWorkerTest
{
    private static readonly TimeSpan SleepTime = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private readonly FakeTimeProvider _time = new();
    private readonly ThreadWorker _threadWorker;
    private int _invocations;

    public ThreadWorkerTest()
    {
        _threadWorker = new ThreadWorker(Count, SleepTime, false, _time);
    }

    private Task Count(CancellationToken token)
    {
        Interlocked.Increment(ref _invocations);
        return Task.CompletedTask;
    }

    private int Invocations => Volatile.Read(ref _invocations);

    private Task RunsReach(int count) => Wait.UntilAsync(() => Invocations == count, $"The worker never ran {count} times");

    [Fact]
    public async Task ThreadWorker_RunsActionWhenStarted()
    {
        await _threadWorker.Restart();

        await RunsReach(1);
        Assert.Equal(1, Invocations);
    }

    [Fact]
    public async Task ThreadWorker_Loops()
    {
        var invocations = 0;
        ThreadWorker? worker = null;
        worker = new ThreadWorker(async _ =>
        {
            invocations++;
            if (invocations == 2)
                await worker!.Stop();
        }, SleepTime, false, _time);

        await worker.Restart();

        await Wait.UntilAsync(_time, SleepTime, () => !worker.IsRunning, "The worker never ran a second pass");
        Assert.Equal(2, invocations);
    }

    [Fact]
    public async Task ThreadWorker_Restarts()
    {
        await _threadWorker.Restart();
        await RunsReach(1);
        await _threadWorker.Restart();

        await RunsReach(2);
        Assert.Equal(2, Invocations);
    }

    [Fact]
    public async Task ThreadWorker_CanBeStoppedAndRestartedManyTimes()
    {
        await _threadWorker.Restart();
        await RunsReach(1);
        await _threadWorker.Stop();
        await _threadWorker.Stop();
        await _threadWorker.Restart();
        await RunsReach(2);
        await _threadWorker.Restart();
        await RunsReach(3);
        await _threadWorker.Stop();
        await _threadWorker.Stop();
        await _threadWorker.Restart();
        await RunsReach(4);

        await Wait.UntilAsync(_time, SleepTime, () => Invocations == 5, "The restarted worker never looped");
        Assert.Equal(5, Invocations);
    }

    [Fact]
    public async Task ThreadWorker_SurvivesAThrowingAction()
    {
        var invocations = 0;
        var worker = new ThreadWorker(_ =>
        {
            invocations++;
            if (invocations == 1)
                throw new InvalidOperationException("Poll failed");
            return Task.CompletedTask;
        }, SleepTime, false, _time);

        await worker.Restart();

        await Wait.UntilAsync(_time, FailureBackoff, () => invocations >= 2, "The worker never resumed after the exception");
        Assert.Single(worker.Errors, e => e.Exception is InvalidOperationException);
        await worker.Stop();
    }

    [Fact]
    public async Task ThreadWorker_TreatsUnrelatedCancellationAsAFailure()
    {
        var invocations = 0;
        var worker = new ThreadWorker(_ =>
        {
            invocations++;
            if (invocations == 1)
                throw new TaskCanceledException("Client-library timeout");
            return Task.CompletedTask;
        }, SleepTime, false, _time);

        await worker.Restart();

        await Wait.UntilAsync(_time, FailureBackoff, () => invocations >= 2, "An unrelated cancellation stopped the worker");
        await worker.Stop();
    }

    [Fact]
    public async Task Stop_DuringTheFailureBackoff_StopsPromptly()
    {
        var invocations = 0;
        var worker = new ThreadWorker(_ =>
        {
            invocations++;
            throw new InvalidOperationException("Always failing");
        }, TimeSpan.Zero, false, _time);
        await worker.Restart();
        await Wait.UntilAsync(() => invocations >= 1, "The worker never ran");

        await worker.Stop().WaitAsync(StopTimeout);

        Assert.False(worker.IsRunning);
    }

    [Fact]
    public async Task Restart_FromInsideTheAction_DoesNotDeadlock()
    {
        var invocations = 0;
        ThreadWorker? worker = null;
        worker = new ThreadWorker(async _ =>
        {
            invocations++;
            if (invocations == 1)
                await worker!.Restart();
        }, SleepTime, false, _time);

        await worker.Restart();

        await Wait.UntilAsync(_time, SleepTime, () => invocations >= 2, "The self-restart deadlocked the worker");
        await worker.Stop();
    }

    [Fact]
    public async Task ThreadWorker_ReportsRunningWhenStarted()
    {
        await _threadWorker.Restart();
        Assert.True(_threadWorker.IsRunning);
    }

    [Fact]
    public async Task ThreadWorker_ReportsNotRunningWhenStopped()
    {
        await _threadWorker.Restart();
        await _threadWorker.Stop();
        Assert.False(_threadWorker.IsRunning);
    }
}
