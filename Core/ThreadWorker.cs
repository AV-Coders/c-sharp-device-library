using System.Text;

namespace AVCoders.Core;

public class ThreadWorker(Func<CancellationToken, Task> action, TimeSpan sleepTime, bool waitFirst = false)
    : LogBase(GenerateLogClassName(action))
{
    private CancellationTokenSource? _cancellationTokenSource = null;
    private Task? _task;
    private bool _waitFirst = waitFirst;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly AsyncLocal<bool> _isWorkerTask = new();

    public bool IsRunning
    {
        get 
        {
            var cts = _cancellationTokenSource;
            var t = _task;
            if (cts == null || t == null)
                return false;
        
            return !cts.IsCancellationRequested && 
               (t.Status == TaskStatus.Running || t.Status == TaskStatus.WaitingForActivation || t.Status == TaskStatus.WaitingToRun);
        }
    }

    public async Task Restart()
    {
        await _lock.WaitAsync();
        try
        {
            await StopInternal();
            StartInternal();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task Stop()
    {
        if (_isWorkerTask.Value)
        {
            _cancellationTokenSource?.Cancel();
            return;
        }

        await _lock.WaitAsync();
        try
        {
            await StopInternal();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task StopInternal()
    {
        try
        {
            if (_cancellationTokenSource == null) 
                return;
            
            _cancellationTokenSource.Cancel();
            if (_task != null)
                await _task;
            
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
            _task = null;
        }
        catch (OperationCanceledException)
        {
            // Do nothing
        }
        catch (Exception e)
        {
            LogException(e, "Error while stopping ThreadWorker");
        }
    }

    private void StartInternal()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;
        _task = Task.Run(async () =>
        {
            _isWorkerTask.Value = true;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (_waitFirst)
                        await Task.Delay(sleepTime, token);
                    await action.Invoke(token);
                    if (!_waitFirst)
                        await Task.Delay(sleepTime, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Task was cancelled
            }
            catch (Exception e)
            {
                LogException(e,
                    $"ThreadWorker has encountered an exception while running {action.Method.Name} in {action.Target?.GetType().Name ?? "*Class could not be determined*"}");
            }
            finally
            {
                _isWorkerTask.Value = false;
            }
        }, token);
    }

    ~ThreadWorker()
    {
        _cancellationTokenSource?.Cancel();
    }

    public void WaitFirst(bool waitFirst)
    {
        _waitFirst = waitFirst;
    }
    
    private static string GenerateLogClassName(Func<CancellationToken, Task> action)
    {
        StringBuilder sb = new StringBuilder();
        if (action.Target != null)
        {
            sb.Append(action.Target.GetType().Name);
            sb.Append('.');
        }
        
        sb.Append(action.Method.Name);

        if (action.Target is LogBase targetInstance) 
            sb.Append($" ({targetInstance.Name})");

        return sb.ToString();
    }
}