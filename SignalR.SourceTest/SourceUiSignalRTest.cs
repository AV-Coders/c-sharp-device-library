using Microsoft.AspNetCore.SignalR;

namespace AVCoders.SignalR.Source.Tests;

public class SourceUiSignalRTest
{
    private static readonly SourceDefinition None = new("None", "Not off, not on", "None", "off");
    private static readonly SourceDefinition Laptop = new("Laptop", "Lectern HDMI", "laptop", "laptop");
    private static readonly SourceDefinition Wireless = new("Wireless", "Wireless Presenter", "wireless", "wifi");

    private readonly List<SourceDefinition> _sources = [None, Laptop, Wireless];
    private readonly SourceManager _manager;
    private readonly string _groupName;
    private readonly Mock<ISourceHub> _groupClient = new();
    private readonly Mock<IHubClients<ISourceHub>> _hubClients = new();
    private readonly Mock<IHubContext<SourceHub, ISourceHub>> _hubContext = new();
    private readonly SourceUiSignalR _ui;

    public SourceUiSignalRTest()
    {
        _groupName = $"ui-src-{Guid.NewGuid()}";
        _manager = new SourceManager(_groupName, _sources, defaultSource: "Laptop");
        _hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupClient.Object);
        _hubContext.Setup(h => h.Clients).Returns(_hubClients.Object);

        _ui = new SourceUiSignalR(_manager, _hubContext.Object);
    }

    [Fact]
    public async Task Constructor_RegistersManagerWithHub()
    {
        var harness = SourceHubTestHarness.CreateHub();

        await harness.Hub.JoinGroup(_groupName);

        harness.GroupsMock.Verify(g => g.AddToGroupAsync(
            It.IsAny<string>(), _groupName, It.IsAny<CancellationToken>()), Times.Once);
        harness.CallerMock.Verify(c => c.UpdateSourceList(
            It.Is<List<SourceDefinition>>(l => l.SequenceEqual(_sources))), Times.Once);
    }

    [Fact]
    public void ManagerSourceIndexChanged_NotifiesHubGroup()
    {
        _manager.SetCurrentSource(2);

        _hubClients.Verify(c => c.Group(_groupName), Times.AtLeastOnce);
        _groupClient.Verify(c => c.UpdateSourceIndex(2), Times.Once);
    }

    [Fact]
    public void PowerOn_DrivesManagerToDefaultSourceAndPropagatesIndex()
    {
        _ui.PowerOn();

        Assert.Equal("laptop", _manager.CurrentSource);
        _groupClient.Verify(c => c.UpdateSourceIndex(1), Times.Once);
    }

    [Fact]
    public void PowerOff_DrivesManagerToOffSourceAndPropagatesIndex()
    {
        _ui.PowerOn();
        _groupClient.Invocations.Clear();

        _ui.PowerOff();

        Assert.Equal("None", _manager.CurrentSource);
        _groupClient.Verify(c => c.UpdateSourceIndex(0), Times.Once);
    }

    [Fact]
    public void Name_MatchesManagerName()
    {
        Assert.Equal(_manager.Name, _ui.Name);
    }
}
