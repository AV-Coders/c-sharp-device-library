namespace AVCoders.Core;

public class ThreadWorker
{
    private readonly Action _action;
    private Guid _runInstance = Guid.Empty;
    
    public ThreadWorker(Action action)
    {
        _action = action;
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