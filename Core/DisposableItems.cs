namespace AVCoders.Core;

public class DisposableItems(IEnumerable<IDisposable> disposables) : IDisposable
{
    public void Dispose()
    {
        foreach (var disposable in disposables)
        {
            disposable.Dispose();
        }
    }
}