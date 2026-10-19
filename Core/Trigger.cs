namespace AVCoders.Core;

public static class Trigger
{
    private const long IdRange = 0x1_0000_0000;
    private const string IdFormat = "x8";
    private static readonly AsyncLocal<string?> Ambient = new();

    public static string? Id => Ambient.Value;

    public static string Start()
    {
        var id = Random.Shared.NextInt64(IdRange).ToString(IdFormat);
        Ambient.Value = id;
        return id;
    }

    public static IDisposable Begin()
    {
        var previous = Ambient.Value;
        Start();
        return new Restore(previous);
    }

    public static IDisposable Resume(string? id)
    {
        var previous = Ambient.Value;
        Ambient.Value = id ?? previous;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
