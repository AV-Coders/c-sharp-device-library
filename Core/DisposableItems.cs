namespace AVCoders.Core;

public class DisposableItems(IEnumerable<IDisposable> disposables) : IDisposable
{
    public void Dispose()
    {
        // Disposed in reverse so nested scopes (e.g. Serilog LogContext bookmarks) unwind correctly.
        foreach (var disposable in disposables.Reverse())
        {
            disposable.Dispose();
        }
    }
}