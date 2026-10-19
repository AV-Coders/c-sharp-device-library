namespace AVCoders.Core.Tests;

public class ThreadWorkerTriggerTest
{
    private static readonly TimeSpan SleepTime = TimeSpan.FromMilliseconds(50);
    private readonly List<string?> _seen = [];
    private readonly object _seenLock = new();

    private List<string?> Seen()
    {
        lock (_seenLock)
            return _seen.ToList();
    }

    [Fact]
    public async Task StartedUnderATrigger_EachPassGetsItsOwnId()
    {
        var worker = new ThreadWorker(_ =>
        {
            lock (_seenLock)
                _seen.Add(Trigger.Id);
            return Task.CompletedTask;
        }, SleepTime);
        var started = Trigger.Start();

        await worker.Restart();
        await Wait.UntilAsync(() => Seen().Count >= 2, "The worker never ran twice");
        await worker.Stop();

        var seen = Seen();
        Assert.All(seen, Assert.NotNull);
        Assert.DoesNotContain(started, seen);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(started, Trigger.Id);
    }
}
