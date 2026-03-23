namespace AVCoders.Core;

public static class LogBaseRegistry
{
    private static readonly List<LogBase> Instances = [];
    private static readonly object Lock = new();

    public static void Register(LogBase instance)
    {
        lock (Lock)
        {
            Instances.Add(instance);
        }
    }

    public static void Deregister(LogBase instance)
    {
        lock (Lock)
        {
            Instances.Remove(instance);
        }
    }

    public static IReadOnlyList<LogBase> GetAll()
    {
        lock (Lock)
        {
            return Instances.ToList();
        }
    }

    public static void ClearEvents()
    {
        foreach (var instance in GetAll())
            instance.ClearEvents();
    }

    public static void ClearErrors()
    {
        foreach (var instance in GetAll())
            instance.ClearErrors();
    }

    public static void SetEventLimits(int limit)
    {
        foreach (var instance in GetAll())
            instance.SetEventLimit(limit);
    }

    public static void SetErrorLimits(int limit)
    {
        foreach (var instance in GetAll())
            instance.SetErrorLimit(limit);
    }
}
