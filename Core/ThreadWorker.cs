namespace AVCoders.Core;

public class ThreadWorker
{
    private readonly Action _action;
    private readonly TimeSpan _sleepTime;
    private CancellationTokenSource _cancellationTokenSource;
    private Task? _task;

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
        _task = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    _action.Invoke();
                    await Task.Delay(_sleepTime, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Task was cancelled
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }, token);
    }

    public void Stop()
    {
        _cancellationTokenSource.Cancel();
        _task?.Wait();
        _cancellationTokenSource.Dispose();
    }

    ~ThreadWorker()
    {
        Stop();
    }
}