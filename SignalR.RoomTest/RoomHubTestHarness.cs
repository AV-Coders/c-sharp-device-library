using Microsoft.AspNetCore.SignalR;

namespace AVCoders.SignalR.Room.Tests;

public class RoomHubTestHarness
{
    public RoomHub Hub { get; }
    public Mock<IRoomHub> CallerMock { get; } = new();
    public Mock<IHubCallerClients<IRoomHub>> ClientsMock { get; } = new();
    public Mock<IGroupManager> GroupsMock { get; } = new();
    public Mock<HubCallerContext> ContextMock { get; } = new();

    private RoomHubTestHarness()
    {
        ClientsMock.Setup(c => c.Caller).Returns(CallerMock.Object);
        ContextMock.Setup(c => c.ConnectionId).Returns($"conn-{Guid.NewGuid()}");
        Hub = new RoomHub
        {
            Clients = ClientsMock.Object,
            Groups = GroupsMock.Object,
            Context = ContextMock.Object,
        };
    }

    public static RoomHubTestHarness CreateHub() => new();
}
