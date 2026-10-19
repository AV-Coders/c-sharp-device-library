namespace AVCoders.Core;

public enum ActorKind
{
    Browser,
    Mcp,
    Panel
}

public record Actor(ActorKind Kind, string Ip, string UserAgent, string? HostName, string ConnectionId)
{
    private static readonly AsyncLocal<Actor?> Ambient = new();

    public static Actor? Current
    {
        get => Ambient.Value;
        set => Ambient.Value = value;
    }

    public override string ToString() => HostName == null ? $"{Kind} {Ip}" : $"{Kind} {Ip} ({HostName})";
}
