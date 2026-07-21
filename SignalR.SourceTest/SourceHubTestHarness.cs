using Microsoft.AspNetCore.SignalR;

namespace AVCoders.SignalR.Source.Tests;

/// <summary>
/// Wraps a <see cref="SourceHub"/> with mocked SignalR plumbing so the hub
/// methods can be invoked from tests.
/// </summary>
public class SourceHubTestHarness
{
    public SourceHub Hub { get; }
    public Mock<ISourceHub> CallerMock { get; } = new();
    public Mock<IHubCallerClients<ISourceHub>> ClientsMock { get; } = new();
    public Mock<IGroupManager> GroupsMock { get; } = new();
    public Mock<HubCallerContext> ContextMock { get; } = new();

    private SourceHubTestHarness()
    {
        ClientsMock.Setup(c => c.Caller).Returns(CallerMock.Object);
        ContextMock.Setup(c => c.ConnectionId).Returns($"conn-{Guid.NewGuid()}");
        Hub = new SourceHub
        {
            Clients = ClientsMock.Object,
            Groups = GroupsMock.Object,
            Context = ContextMock.Object,
        };
    }

    public static SourceHubTestHarness CreateHub() => new();
}
