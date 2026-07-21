using AVCoders.Camera;
using AVCoders.Core;
using Microsoft.AspNetCore.SignalR;

namespace AVCoders.SignalR.Camera.Tests;

public class CameraUiSignalRTest
{
    private readonly TestCamera _camera;
    private readonly CameraManager _manager;
    private readonly Mock<ICameraHub> _groupClient = new();
    private readonly Mock<IHubClients<ICameraHub>> _hubClients = new();
    private readonly Mock<IHubContext<CameraHub, ICameraHub>> _hubContext = new();
    private readonly CameraUiSignalR _ui;

    public CameraUiSignalRTest()
    {
        _camera = new TestCamera($"UiCam-{Guid.NewGuid()}");
        _manager = new CameraManager(_camera);
        _hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupClient.Object);
        _hubContext.Setup(h => h.Clients).Returns(_hubClients.Object);

        _ui = new CameraUiSignalR(_manager, _hubContext.Object);
    }

    [Fact]
    public void PowerOn_ForwardsToCamera()
    {
        _ui.PowerOn();

        Assert.Equal(1, _camera.PowerOnCallCount);
    }

    [Fact]
    public void PowerOff_ForwardsToCamera()
    {
        _ui.PowerOff();

        Assert.Equal(1, _camera.PowerOffCallCount);
    }

    [Fact]
    public void ManagerPowerStateChange_NotifiesHubGroup()
    {
        _camera.SetPowerStateForTest(PowerState.On);

        _hubClients.Verify(c => c.Group(_manager.Name), Times.AtLeastOnce);
        _groupClient.Verify(c => c.OnPowerStateChanged(PowerState.On), Times.Once);
    }

    [Fact]
    public void ManagerPresetRecalled_NotifiesHubGroup()
    {
        _camera.SetLastRecalledPresetForTest(7);

        _groupClient.Verify(c => c.OnPresetRecalled(7), Times.Once);
    }

    [Fact]
    public void ManagerPresetCleared_NotifiesHubGroup()
    {
        _camera.SetLastRecalledPresetForTest(2);
        _groupClient.Invocations.Clear();

        _camera.PanTiltStop();

        _groupClient.Verify(c => c.OnPresetCleared(), Times.Once);
    }

    [Fact]
    public void ManagerTrackingModeChanged_NotifiesHubGroup()
    {
        var tracking = new TrackingTestCamera($"UiTrackingCam-{Guid.NewGuid()}");
        var manager = new CameraManager(tracking);
        var groupClient = new Mock<ICameraHub>();
        var hubClients = new Mock<IHubClients<ICameraHub>>();
        var hubContext = new Mock<IHubContext<CameraHub, ICameraHub>>();
        hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(groupClient.Object);
        hubContext.Setup(h => h.Clients).Returns(hubClients.Object);
        _ = new CameraUiSignalR(manager, hubContext.Object);

        manager.SetTracking(CameraTrackingMode.Auto);

        hubClients.Verify(c => c.Group(manager.Name), Times.AtLeastOnce);
        groupClient.Verify(c => c.OnTrackingModeChanged(CameraTrackingMode.Auto), Times.Once);
    }

    [Fact]
    public void CameraTrackingFeedback_NotifiesHubGroup()
    {
        var tracking = new TrackingTestCamera($"UiTrackingCam-{Guid.NewGuid()}");
        var manager = new CameraManager(tracking);
        var groupClient = new Mock<ICameraHub>();
        var hubClients = new Mock<IHubClients<ICameraHub>>();
        var hubContext = new Mock<IHubContext<CameraHub, ICameraHub>>();
        hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(groupClient.Object);
        hubContext.Setup(h => h.Clients).Returns(hubClients.Object);
        _ = new CameraUiSignalR(manager, hubContext.Object);

        tracking.SetTrackingModeForTest(CameraTrackingMode.Manual);

        groupClient.Verify(c => c.OnTrackingModeChanged(CameraTrackingMode.Manual), Times.Once);
    }

    [Fact]
    public void Constructor_RegistersManagerWithHub()
    {
        // After construction, JoinGroup on the static hub should find this manager.
        // We exercise that by joining via a CameraHub instance with mocked clients/groups.
        var hub = CameraHubTestHarness.CreateHub();
        hub.JoinGroup(_manager.Name).GetAwaiter().GetResult();

        hub.GroupsMock.Verify(g => g.AddToGroupAsync(
            It.IsAny<string>(), _manager.Name, It.IsAny<CancellationToken>()), Times.Once);
        hub.CallerMock.Verify(c => c.OnPresetDefinitionChanged(_manager.PresetDefinitions()), Times.Once);
    }
}
