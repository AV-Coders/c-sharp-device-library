using Microsoft.AspNetCore.SignalR;

namespace AVCoders.SignalR.Destination.Tests;

/// <summary>
/// Wraps a <see cref="DestinationHub"/> with mocked SignalR plumbing so the hub
/// methods can be invoked from tests.
/// </summary>
public class DestinationHubTestHarness
{
    public DestinationHub Hub { get; }
    public Mock<IDestinationHub> CallerMock { get; } = new();
    public Mock<IHubCallerClients<IDestinationHub>> ClientsMock { get; } = new();
    public Mock<IGroupManager> GroupsMock { get; } = new();
    public Mock<HubCallerContext> ContextMock { get; } = new();

    private DestinationHubTestHarness()
    {
        ClientsMock.Setup(c => c.Caller).Returns(CallerMock.Object);
        ContextMock.Setup(c => c.ConnectionId).Returns($"conn-{Guid.NewGuid()}");
        Hub = new DestinationHub
        {
            Clients = ClientsMock.Object,
            Groups = GroupsMock.Object,
            Context = ContextMock.Object,
        };
    }

    public static DestinationHubTestHarness CreateHub() => new();
}
