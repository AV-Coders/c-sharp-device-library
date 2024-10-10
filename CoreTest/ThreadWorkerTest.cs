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
    public void ThreadWorker_RunsActionWhenStarted()
    {
        _threadWorker.Restart();
        
        Thread.Sleep(10);
        
        _actionMock.Verify(action => action(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ThreadWorker_Loops()
    {
        _threadWorker.Restart();
        
        Thread.Sleep(300);
        
        _actionMock.Verify(action => action(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public void ThreadWorker_Restarts()
    {
        _threadWorker.Restart();
        Thread.Sleep(10);
        _threadWorker.Restart();
        
        Thread.Sleep(10);
        
        _actionMock.Verify(action => action(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public void ThreadWorker_CanBeStoppedAndRestartedManyTimes()
    {
        _threadWorker.Restart();
        Thread.Sleep(10);
        _threadWorker.Stop();
        Thread.Sleep(10);
        _threadWorker.Stop();
        Thread.Sleep(10);
        _threadWorker.Restart();
        Thread.Sleep(10);
        _threadWorker.Restart();
        Thread.Sleep(10);
        _threadWorker.Stop();
        Thread.Sleep(10);
        _threadWorker.Stop();
        Thread.Sleep(10);
        _threadWorker.Restart();
        Thread.Sleep(250);
        
        _actionMock.Verify(action => action(It.IsAny<CancellationToken>()), Times.Exactly(5));
    }
}