using Microsoft.AspNetCore.SignalR;

namespace AVCoders.SignalR.Display.Tests;

/// <summary>
/// Wraps a <see cref="DisplayHub"/> with mocked SignalR plumbing so the hub methods
/// can be invoked from tests.
/// </summary>
public class DisplayHubTestHarness
{
    public DisplayHub Hub { get; }
    public Mock<IDisplayHub> CallerMock { get; } = new();
    public Mock<IHubCallerClients<IDisplayHub>> ClientsMock { get; } = new();
    public Mock<IGroupManager> GroupsMock { get; } = new();
    public Mock<HubCallerContext> ContextMock { get; } = new();

    private DisplayHubTestHarness()
    {
        ClientsMock.Setup(c => c.Caller).Returns(CallerMock.Object);
        ContextMock.Setup(c => c.ConnectionId).Returns($"conn-{Guid.NewGuid()}");
        Hub = new DisplayHub
        {
            Clients = ClientsMock.Object,
            Groups = GroupsMock.Object,
            Context = ContextMock.Object,
        };
    }

    public Task JoinGroup(string groupName) => Hub.JoinGroup(groupName);

    public static DisplayHubTestHarness CreateHub() => new();
}
