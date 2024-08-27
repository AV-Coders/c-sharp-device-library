namespace AVCoders.Core;

public class ThreadWorker
{
    private readonly Action _action;
    private readonly TimeSpan _sleepTime;
    private CancellationTokenSource _cancellationTokenSource;

    public ThreadWorker(Action action, TimeSpan sleepTime)
    {
        _action = action;
        _sleepTime = sleepTime;
        _cancellationTokenSource = new CancellationTokenSource();
    }

    public void Restart()
    {
        Stop();
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;
        new Thread(_ =>
        {
            while (!token.IsCancellationRequested)
            {
                _action.Invoke();
                Thread.Sleep(_sleepTime);
            }
        }).Start();
    }

    public void Stop()
    {
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
    }

    ~ThreadWorker()
    {
        Stop();
    }
}