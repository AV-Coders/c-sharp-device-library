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