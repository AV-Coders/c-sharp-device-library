using AVCoders.Core;
using AVCoders.Core.Tests;

namespace AVCoders.MediaPlayer.Tests;

internal static class LogCapture
{
    private static readonly object FactoryLock = new();

    public static ScopeCapturingLoggerFactory Create<T>(Func<T> create, out T instance)
    {
        var logs = new ScopeCapturingLoggerFactory();
        lock (FactoryLock)
        {
            var original = LogBase.LoggerFactory;
            LogBase.LoggerFactory = logs;
            try
            {
                instance = create();
            }
            finally
            {
                LogBase.LoggerFactory = original;
            }
        }
        return logs;
    }
}
