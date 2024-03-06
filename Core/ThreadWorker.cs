namespace AVCoders.Core;

public class ThreadWorker
{
    private readonly Action _action;
    private readonly TimeSpan _sleepTime;
    private Guid _runInstance = Guid.Empty;
    
    public ThreadWorker(Action action, TimeSpan sleepTime)
    {
        _action = action;
        _sleepTime = sleepTime;
    }

    public void Restart()
    {
        var thisRun = Guid.NewGuid();
        _runInstance = thisRun;
        new Thread(_ =>
        {
            while (_runInstance == thisRun)
            {
                _action.Invoke();
                Thread.Sleep(_sleepTime);
            }
        }).Start();
    }

    public void Stop()
    {
        _runInstance = Guid.Empty;
    }

    ~ThreadWorker()
    {
        Stop();
    }
}