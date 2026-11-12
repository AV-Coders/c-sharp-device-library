namespace AVCoders.SignalR.Connections;

public record GroupConnections(string Group, bool Registered, int Connections);

public record UnknownGroupRequest(DateTimeOffset At, string Hub, string Method, string ConnectionId, string Group);

public record HubConnectionsSnapshot(
    string Hub,
    int OpenConnections,
    IReadOnlyList<GroupConnections> Groups,
    IReadOnlyList<UnknownGroupRequest> UnknownGroupRequests);
