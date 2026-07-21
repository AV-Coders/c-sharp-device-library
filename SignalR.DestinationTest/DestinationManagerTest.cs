using AVCoders.Core;
using AVCoders.SignalR.Source;

namespace AVCoders.SignalR.Destination.Tests;

public class DestinationManagerTest
{
    private static readonly SourceDefinition None = new("None", "Not off, not on", "None", "off");
    private static readonly SourceDefinition Laptop = new("Laptop", "Lectern HDMI", "laptop", "laptop");
    private static readonly SourceDefinition Wireless = new("Wireless", "Wireless Presenter", "wireless", "wifi");

    private readonly List<SourceDefinition> _sources = [None, Laptop, Wireless];
    private readonly SourceManager _sourceManager;
    private readonly DestinationManager _destination;

    public DestinationManagerTest()
    {
        _sourceManager = new SourceManager("test-source-mgr", _sources, defaultSource: "Laptop", offSource: "None");
        _destination = new DestinationManager(
            "Display 1", "display-1", "tv", _sourceManager,
            defaultSource: "laptop", offSource: "None");
    }

    [Fact]
    public void Snapshot_ReflectsConfiguredFields()
    {
        var snap = _destination.Snapshot;

        Assert.Equal("Display 1", snap.Name);
        Assert.Equal("display-1", snap.DestinationId);
        Assert.Equal("tv", snap.Icon);
        Assert.Equal(None, snap.CurrentSource);
        Assert.False(snap.VideoMute);
    }

    [Fact]
    public void CurrentSource_DefaultsToNone()
    {
        Assert.Equal(None, _destination.CurrentSource);
    }

    [Fact]
    public void VideoMute_DefaultsToFalse()
    {
        Assert.False(_destination.VideoMute);
    }

    [Fact]
    public void RouteSource_KnownSource_UpdatesCurrentSource()
    {
        _destination.RouteSource("laptop");

        Assert.Equal(Laptop, _destination.CurrentSource);
    }

    [Fact]
    public void RouteSource_KnownSource_SetsPowerOn()
    {
        _destination.RouteSource("laptop");

        Assert.Equal(PowerState.On, _destination.PowerState);
    }

    [Fact]
    public void RouteSource_NoneSource_SetsPowerOff()
    {
        _destination.RouteSource("laptop");
        _destination.RouteSource("None");

        Assert.Equal(None, _destination.CurrentSource);
        Assert.Equal(PowerState.Off, _destination.PowerState);
    }

    [Fact]
    public void RouteSource_UnknownSource_DoesNotChangeState()
    {
        var initial = _destination.CurrentSource;

        _destination.RouteSource("not-a-source");

        Assert.Equal(initial, _destination.CurrentSource);
    }

    [Fact]
    public void RouteSource_UnknownSource_DoesNotRaiseChanged()
    {
        var handler = new Mock<Action<DestinationDefinition>>();
        _destination.OnDestinationChanged += handler.Object;

        _destination.RouteSource("not-a-source");

        handler.Verify(h => h.Invoke(It.IsAny<DestinationDefinition>()), Times.Never);
    }

    [Fact]
    public void RouteSource_RaisesChangedWithSnapshot()
    {
        var handler = new Mock<Action<DestinationDefinition>>();
        _destination.OnDestinationChanged += handler.Object;

        _destination.RouteSource("wireless");

        handler.Verify(h => h.Invoke(It.Is<DestinationDefinition>(d =>
            d.CurrentSource == Wireless &&
            d.Name == "Display 1" &&
            d.DestinationId == "display-1")), Times.Once);
    }

    [Fact]
    public void SetVideoMute_UpdatesFlag()
    {
        _destination.SetVideoMute(true);

        Assert.True(_destination.VideoMute);
    }

    [Fact]
    public void SetVideoMute_RaisesChangedWithSnapshot()
    {
        var handler = new Mock<Action<DestinationDefinition>>();
        _destination.OnDestinationChanged += handler.Object;

        _destination.SetVideoMute(true);

        handler.Verify(h => h.Invoke(It.Is<DestinationDefinition>(d => d.VideoMute)), Times.Once);
    }

    [Fact]
    public void SetVideoMute_DoesNotChangeCurrentSource()
    {
        _destination.RouteSource("laptop");

        _destination.SetVideoMute(true);

        Assert.Equal(Laptop, _destination.CurrentSource);
    }

    [Fact]
    public void PowerOn_RoutesToDefaultSource()
    {
        _destination.PowerOn();

        Assert.Equal(Laptop, _destination.CurrentSource);
        Assert.Equal(PowerState.On, _destination.PowerState);
    }

    [Fact]
    public void PowerOff_RoutesToOffSource()
    {
        _destination.PowerOn();

        _destination.PowerOff();

        Assert.Equal(None, _destination.CurrentSource);
        Assert.Equal(PowerState.Off, _destination.PowerState);
    }
}
