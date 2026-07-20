using Moq;

namespace AVCoders.Core.Tests;

public class ThreadWorkerTest
{
    private readonly ThreadWorker _threadWorker;
    private readonly Mock<Func<CancellationToken, Task>> _actionMock;

    public ThreadWorkerTest()
    {
        _actionMock = new();
        _threadWorker = new ThreadWorker(_actionMock.Object, TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task ThreadWorker_RunsActionWhenStarted()
    {
        await _threadWorker.Restart();
        
        Thread.Sleep(10);
        
        _actionMock.Verify(action => action(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ThreadWorker_Loops()
    {
        await _threadWorker.Restart();
        
        Thread.Sleep(300);
        
        _actionMock.Verify(action => action(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ThreadWorker_Restarts()
    {
        await _threadWorker.Restart();
        Thread.Sleep(10);
        await _threadWorker.Restart();
        
        Thread.Sleep(10);
        
        _actionMock.Verify(action => action(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ThreadWorker_CanBeStoppedAndRestartedManyTimes()
    {
        await _threadWorker.Restart();
        Thread.Sleep(10);
        await _threadWorker.Stop();
        Thread.Sleep(10);
        await _threadWorker.Stop();
        Thread.Sleep(10);
        await _threadWorker.Restart();
        Thread.Sleep(10);
        await _threadWorker.Restart();
        Thread.Sleep(10);
        await _threadWorker.Stop();
        Thread.Sleep(10);
        await _threadWorker.Stop();
        Thread.Sleep(10);
        await _threadWorker.Restart();
        Thread.Sleep(250);
        
        _actionMock.Verify(action => action(It.IsAny<CancellationToken>()), Times.Exactly(5));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failureMessage)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failureMessage);
            await Task.Delay(25);
        }
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
        }, TimeSpan.FromMilliseconds(50));

        await worker.Restart();

        await WaitUntilAsync(() => invocations >= 2, "The worker never resumed after the exception");
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
                throw new TaskCanceledException("Simulated client-library timeout");
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(50));

        await worker.Restart();

        await WaitUntilAsync(() => invocations >= 2, "An unrelated cancellation stopped the worker");
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
        }, TimeSpan.Zero);
        await worker.Restart();
        await WaitUntilAsync(() => invocations >= 1, "The worker never ran");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await worker.Stop();
        stopwatch.Stop();

        Assert.False(worker.IsRunning);
        Assert.True(stopwatch.ElapsedMilliseconds < 900, "Stop waited out the failure backoff instead of cancelling it");
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
        }, TimeSpan.FromMilliseconds(50));

        await worker.Restart();

        await WaitUntilAsync(() => invocations >= 2, "The self-restart deadlocked the worker");
        await worker.Stop();
    }

    [Fact]
    public void ThreadWorker_ReportsNotRunningWhenCreated()
    {
        Assert.False(_threadWorker.IsRunning);
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
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        await _threadWorker.Stop();
        Assert.False(_threadWorker.IsRunning);
    }
}