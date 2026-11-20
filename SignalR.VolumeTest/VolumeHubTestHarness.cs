using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;

namespace AVCoders.SignalR.Volume.Tests;

public class VolumeHubTestHarness
{
    public VolumeHub Hub { get; }
    public Mock<IVolumeHub> CallerMock { get; } = new();
    public Mock<IHubCallerClients<IVolumeHub>> ClientsMock { get; } = new();
    public Mock<IGroupManager> GroupsMock { get; } = new();
    public Mock<HubCallerContext> ContextMock { get; } = new();

    private VolumeHubTestHarness()
    {
        ClientsMock.Setup(c => c.Caller).Returns(CallerMock.Object);
        ContextMock.Setup(c => c.ConnectionId).Returns($"conn-{Guid.NewGuid()}");
        ContextMock.Setup(c => c.Features).Returns(new FeatureCollection());
        Hub = new VolumeHub
        {
            Clients = ClientsMock.Object,
            Groups = GroupsMock.Object,
            Context = ContextMock.Object,
        };
    }

    public static VolumeHubTestHarness CreateHub() => new();
}
