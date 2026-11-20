namespace AVCoders.Core;

public enum ActorKind
{
    Browser,
    Mcp,
    Panel,
    Schedule,
    System,
    Console,
    Peer
}

public record Actor(ActorKind Kind, string Ip, string UserAgent, string? HostName, string ConnectionId)
{
    private static readonly AsyncLocal<Actor?> Ambient = new();

    public static Actor? Current
    {
        get => Ambient.Value;
        set => Ambient.Value = value;
    }

    public string Name { get; init; } = string.Empty;

    public bool IsPerson => Kind is ActorKind.Browser or ActorKind.Mcp or ActorKind.Panel or ActorKind.Console or ActorKind.Peer;

    public static Actor Schedule(string name) => new(ActorKind.Schedule, string.Empty, string.Empty, null, string.Empty) { Name = name };

    public static Actor System(string name) => new(ActorKind.System, string.Empty, string.Empty, null, string.Empty) { Name = name };

    public static Actor Console(string command) => new(ActorKind.Console, string.Empty, string.Empty, null, string.Empty) { Name = command };

    public static Actor Peer(string ip, string? hostName, string connectionId) => new(ActorKind.Peer, ip, string.Empty, hostName, connectionId);

    public static IDisposable Begin(Actor actor)
    {
        var previous = Ambient.Value;
        Ambient.Value = actor;
        return new Restore(previous);
    }

    public override string ToString() => Kind switch
    {
        ActorKind.Schedule or ActorKind.System or ActorKind.Console => $"{Kind} {Name}",
        _ => HostName == null ? $"{Kind} {Ip}" : $"{Kind} {Ip} ({HostName})"
    };

    private sealed class Restore(Actor? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
