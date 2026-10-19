namespace AVCoders.Core.Tests;

public static class Wait
{
    private static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    public static async Task UntilAsync(Func<bool> condition, string failureMessage)
    {
        var deadline = DateTime.UtcNow + MaximumWait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failureMessage);
            await Task.Delay(PollInterval);
        }
    }
}
