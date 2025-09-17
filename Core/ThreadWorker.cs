namespace AVCoders.Core;

public class ThreadWorker(Func<CancellationToken, Task> action, TimeSpan sleepTime, bool waitFirst = false) : LogBase("ThreadWorker")
{
    private CancellationTokenSource? _cancellationTokenSource = null;
    private Task? _task;
    private bool _waitFirst = waitFirst;

    public void Restart()
    {
        Stop();
        Start();
    }

    public Task Stop()
    {
        try
        {
            if (_cancellationTokenSource == null) 
                return Task.CompletedTask;
            
            _cancellationTokenSource.Cancel();
            _task?.Wait();
            
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null; 
        }
        catch (AggregateException)
        {
            // Do nothing
        }
        catch (TaskCanceledException)
        {
            // Do nothing
        }
        
        return Task.CompletedTask;
    }

    private void Start()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;
        _task = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if(_waitFirst)
                        await Task.Delay(sleepTime, token);
                    await action.Invoke(token);
                    if(!_waitFirst)
                        await Task.Delay(sleepTime, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Task was cancelled
            }
            catch (Exception e)
            {
                LogException(e, "ThreadWorker has encountered an exception while running a task:");
            }
        }, token);
    }

    ~ThreadWorker()
    {
        Stop();
    }

    public void WaitFirst(bool waitFirst)
    {
        _waitFirst = waitFirst;
    }
}