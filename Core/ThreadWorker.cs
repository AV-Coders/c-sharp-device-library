namespace AVCoders.Core;

public class ThreadWorker
{
    private readonly Func<CancellationToken, Task> _action;
    private readonly TimeSpan _sleepTime;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _task;

    public ThreadWorker(Func<CancellationToken, Task> action, TimeSpan sleepTime)
    {
        _action = action;
        _sleepTime = sleepTime;
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
        catch (AggregateException ex)
        {
            Console.WriteLine(ex);
        }
        catch (TaskCanceledException e)
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
                    await _action.Invoke(token);
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

    ~ThreadWorker()
    {
        Stop();
    }
}