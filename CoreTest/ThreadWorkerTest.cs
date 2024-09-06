using Moq;

namespace AVCoders.Core.Tests;

public class ThreadWorkerTest
{
    private readonly ThreadWorker _threadWorker;
    private readonly Mock<Action> _actionMock;

    public ThreadWorkerTest()
    {
        _actionMock = new();
        _threadWorker = new ThreadWorker(_actionMock.Object, TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void ThreadWorker_RunsActionWhenStarted()
    {
        _threadWorker.Restart();
        
        Thread.Sleep(100);
        
        _actionMock.Verify(action => action(), Times.Once);
    }

    [Fact]
    public void ThreadWorker_Loops()
    {
        _threadWorker.Restart();
        
        Thread.Sleep(300);
        
        _actionMock.Verify(action => action(), Times.Exactly(2));
    }

    [Fact]
    public void ThreadWorker_Restarts()
    {
        _threadWorker.Restart();
        Thread.Sleep(10);
        _threadWorker.Restart();
        
        Thread.Sleep(100);
        
        _actionMock.Verify(action => action(), Times.Exactly(2));
    }

    [Fact]
    public void ThreadWorker_CanBeStoppedManyTimes()
    {
        _threadWorker.Restart();
        Thread.Sleep(10);
        _threadWorker.Stop();
        Thread.Sleep(10);
        _threadWorker.Stop();
        Thread.Sleep(10);
        _threadWorker.Restart();
    }
}