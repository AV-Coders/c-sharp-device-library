using AVCoders.Core;
using AVCoders.Core.Tests;

namespace AVCoders.Dsp.Tests;

internal static class Logged
{
    public static T Create<T>(ScopeCapturingLoggerFactory logs, Func<T> create)
    {
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = logs;
        try
        {
            return create();
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }
}
