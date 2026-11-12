using System.Collections.Concurrent;

namespace AVCoders.SignalR.Connections.Tests;

public interface ITestHub
{
    Task Joined(string groupName);
}

#pragma warning disable S2325 // Methods should be static
public class TestHub() : GroupHub<ITestHub>(Connections)
{
    private static readonly ConcurrentDictionary<string, bool> RegisteredGroups = new();

    public static HubConnections Connections { get; } = new(nameof(TestHub));

    public static void RegisterGroup(string groupName)
    {
        RegisteredGroups[groupName] = true;
        Connections.Register(groupName);
    }

    public async Task JoinGroup(string groupName)
    {
        await AddToGroup(groupName);
        if (!RegisteredGroups.ContainsKey(groupName))
        {
            ReportUnknownGroup(groupName);
            return;
        }
        await Clients.Caller.Joined(groupName);
    }

    public void PowerOn(string groupName)
    {
        if (!RegisteredGroups.ContainsKey(groupName))
            ReportUnknownGroup(groupName);
    }
}
