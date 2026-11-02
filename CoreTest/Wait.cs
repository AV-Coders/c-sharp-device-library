using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace AVCoders.Core.Tests;

public static class Wait
{
    private static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(2);
    private static readonly TimeSpan RealTimePause = TimeSpan.FromMilliseconds(1);

    public static async Task UntilAsync(Func<bool> condition, string failureMessage)
    {
        var waiting = Stopwatch.StartNew();
        while (!await SettlesAsync(condition))
        {
            Assert.True(waiting.Elapsed < MaximumWait, failureMessage);
            await Task.Delay(RealTimePause);
        }
    }

    public static async Task UntilAsync(FakeTimeProvider time, TimeSpan step, Func<bool> condition, string failureMessage)
    {
        var waiting = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(waiting.Elapsed < MaximumWait, failureMessage);
            await Task.Run(() => time.Advance(step));
            if (await SettlesAsync(condition))
                return;
        }
    }

    private static async Task<bool> SettlesAsync(Func<bool> condition)
    {
        var settling = Stopwatch.StartNew();
        while (!condition())
        {
            if (settling.Elapsed >= SettleTime)
                return false;
            await Task.Yield();
        }
        return true;
    }
}
