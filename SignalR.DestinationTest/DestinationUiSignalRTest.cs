using AVCoders.SignalR.Source;
using Microsoft.AspNetCore.SignalR;

namespace AVCoders.SignalR.Destination.Tests;

public class DestinationUiSignalRTest
{
    private static readonly SourceDefinition None = new("None", "Not off, not on", "None", "off");
    private static readonly SourceDefinition Laptop = new("Laptop", "Lectern HDMI", "laptop", "laptop");
    private static readonly SourceDefinition Wireless = new("Wireless", "Wireless Presenter", "wireless", "wifi");

    private readonly List<SourceDefinition> _sources = [None, Laptop, Wireless];
    private readonly SourceManager _sourceManager;
    private readonly DestinationManager _destination;
    private readonly string _groupName;
    private readonly Mock<IDestinationHub> _groupClient = new();
    private readonly Mock<IHubClients<IDestinationHub>> _hubClients = new();
    private readonly Mock<IHubContext<DestinationHub, IDestinationHub>> _hubContext = new();
    private readonly DestinationUiSignalR _ui;

    public DestinationUiSignalRTest()
    {
        _groupName = $"ui-dst-{Guid.NewGuid()}";
        _sourceManager = new SourceManager($"src-{_groupName}", _sources);
        _destination = new DestinationManager(
            _groupName, "display-1", "tv", _sourceManager,
            defaultSource: "laptop", offSource: "None");

        _hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupClient.Object);
        _hubContext.Setup(h => h.Clients).Returns(_hubClients.Object);

        _ui = new DestinationUiSignalR(_destination, _hubContext.Object);
    }

    [Fact]
    public async Task Constructor_RegistersManagerWithHub()
    {
        var harness = DestinationHubTestHarness.CreateHub();

        await harness.Hub.JoinGroup(_groupName);

        harness.GroupsMock.Verify(g => g.AddToGroupAsync(
            It.IsAny<string>(), _groupName, It.IsAny<CancellationToken>()), Times.Once);
        harness.CallerMock.Verify(c => c.OnDestinationChanged(
            It.Is<DestinationDefinition>(d => d.Name == _groupName)), Times.Once);
    }

    [Fact]
    public void ManagerRouteSource_NotifiesHubGroup()
    {
        _destination.RouteSource("wireless");

        _hubClients.Verify(c => c.Group(_groupName), Times.AtLeastOnce);
        _groupClient.Verify(c => c.OnDestinationChanged(
            It.Is<DestinationDefinition>(d => d.CurrentSource == Wireless)), Times.Once);
    }

    [Fact]
    public void ManagerSetVideoMute_NotifiesHubGroup()
    {
        _destination.SetVideoMute(true);

        _hubClients.Verify(c => c.Group(_groupName), Times.AtLeastOnce);
        _groupClient.Verify(c => c.OnDestinationChanged(
            It.Is<DestinationDefinition>(d => d.VideoMute)), Times.Once);
    }

    [Fact]
    public void PowerOn_DrivesManagerToDefaultSourceAndPropagatesSnapshot()
    {
        _ui.PowerOn();

        Assert.Equal(Laptop, _destination.CurrentSource);
        _groupClient.Verify(c => c.OnDestinationChanged(
            It.Is<DestinationDefinition>(d => d.CurrentSource == Laptop)), Times.Once);
    }

    [Fact]
    public void PowerOff_DrivesManagerToOffSourceAndPropagatesSnapshot()
    {
        _ui.PowerOn();
        _groupClient.Invocations.Clear();

        _ui.PowerOff();

        Assert.Equal(None, _destination.CurrentSource);
        _groupClient.Verify(c => c.OnDestinationChanged(
            It.Is<DestinationDefinition>(d => d.CurrentSource == None)), Times.Once);
    }

    [Fact]
    public void Name_MatchesManagerName()
    {
        Assert.Equal(_destination.Name, _ui.Name);
    }
}
