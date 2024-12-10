namespace AVCoders.Core;

public class ThreadWorker
{
    private readonly Func<CancellationToken, Task> _action;
    private readonly TimeSpan _sleepTime;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _task;
    private bool _waitFirst;

    public ThreadWorker(Func<CancellationToken, Task> action, TimeSpan sleepTime, bool waitFirst = false)
    {
        _action = action;
        _sleepTime = sleepTime;
        _waitFirst = waitFirst;
        _cancellationTokenSource = null;
    }
    
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
                        await Task.Delay(_sleepTime, token);
                    await _action.Invoke(token);
                    if(!_waitFirst)
                        await Task.Delay(_sleepTime, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Task was cancelled
            }
            catch (Exception e)
            {
                Console.WriteLine("ThreadWorker has encountered an exception while running a task:");
                Console.WriteLine(e.Message);
                Console.WriteLine(e.StackTrace ?? "No stack trace available");
                if (e.InnerException == null)
                    return;
                Console.WriteLine(e.InnerException.Message);
                Console.WriteLine(e.InnerException.StackTrace?? "No stack trace available");
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