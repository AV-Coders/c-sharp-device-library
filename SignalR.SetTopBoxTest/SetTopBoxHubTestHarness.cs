using Microsoft.AspNetCore.SignalR;

namespace AVCoders.SignalR.SetTopBox.Tests;

/// <summary>
/// Wraps a <see cref="SetTopBoxHub"/> with mocked SignalR plumbing so the hub
/// methods can be invoked from tests.
/// </summary>
public class SetTopBoxHubTestHarness
{
    public SetTopBoxHub Hub { get; }
    public Mock<ISetTopBoxHub> CallerMock { get; } = new();
    public Mock<IHubCallerClients<ISetTopBoxHub>> ClientsMock { get; } = new();
    public Mock<IGroupManager> GroupsMock { get; } = new();
    public Mock<HubCallerContext> ContextMock { get; } = new();

    private SetTopBoxHubTestHarness()
    {
        ClientsMock.Setup(c => c.Caller).Returns(CallerMock.Object);
        ContextMock.Setup(c => c.ConnectionId).Returns($"conn-{Guid.NewGuid()}");
        Hub = new SetTopBoxHub
        {
            Clients = ClientsMock.Object,
            Groups = GroupsMock.Object,
            Context = ContextMock.Object,
        };
    }

    public static SetTopBoxHubTestHarness CreateHub() => new();
}
