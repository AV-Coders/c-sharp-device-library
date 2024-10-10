namespace AVCoders.Core;

public class ThreadWorker
{
    private readonly Action<CancellationToken> _action;
    private readonly TimeSpan _sleepTime;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _task;

    public ThreadWorker(Action<CancellationToken> action, TimeSpan sleepTime)
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

    public void Stop()
    {
        if (_cancellationTokenSource == null) 
            return;
        
        _cancellationTokenSource.Cancel();
        try
        {
            _task?.Wait();
        }
        catch (AggregateException ex)
        {
            Console.WriteLine(ex);
        }
        _cancellationTokenSource.Dispose();
        _cancellationTokenSource = null;
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
                    _action.Invoke(token);
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