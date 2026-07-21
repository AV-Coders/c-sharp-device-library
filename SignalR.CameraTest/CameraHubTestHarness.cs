using Microsoft.AspNetCore.SignalR;

namespace AVCoders.SignalR.Camera.Tests;

/// <summary>
/// Wraps a <see cref="CameraHub"/> with mocked SignalR plumbing so the hub methods
/// can be invoked from tests.
/// </summary>
public class CameraHubTestHarness
{
    public CameraHub Hub { get; }
    public Mock<ICameraHub> CallerMock { get; } = new();
    public Mock<IHubCallerClients<ICameraHub>> ClientsMock { get; } = new();
    public Mock<IGroupManager> GroupsMock { get; } = new();
    public Mock<HubCallerContext> ContextMock { get; } = new();

    private CameraHubTestHarness()
    {
        ClientsMock.Setup(c => c.Caller).Returns(CallerMock.Object);
        ContextMock.Setup(c => c.ConnectionId).Returns($"conn-{Guid.NewGuid()}");
        Hub = new CameraHub
        {
            Clients = ClientsMock.Object,
            Groups = GroupsMock.Object,
            Context = ContextMock.Object,
        };
    }

    public Task JoinGroup(string groupName) => Hub.JoinGroup(groupName);

    public static CameraHubTestHarness CreateHub() => new();
}
