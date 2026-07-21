using AVCoders.Core;

namespace AVCoders.SignalR.Source.Tests;

public class SourceManagerTest
{
    private static readonly SourceDefinition None = new("None", "Not off, not on", "None", "off");
    private static readonly SourceDefinition Laptop = new("Laptop", "Lectern HDMI", "laptop", "laptop");
    private static readonly SourceDefinition Wireless = new("Wireless", "Wireless Presenter", "wireless", "wifi");

    private readonly List<SourceDefinition> _sources = [None, Laptop, Wireless];
    private readonly SourceManager _manager;

    public SourceManagerTest()
    {
        _manager = new SourceManager("test-room", _sources, defaultSource: "Laptop", offSource: "None");
    }

    [Fact]
    public void Sources_ReturnsCopyOfConfiguredSources()
    {
        var sources = _manager.Sources;

        Assert.Equal(_sources, sources);
        Assert.NotSame(_sources, sources);
    }

    [Fact]
    public void CurrentSource_DefaultsToNone()
    {
        Assert.Equal("None", _manager.CurrentSource);
    }

    [Fact]
    public void SetCurrentSource_ByIndex_UpdatesCurrentSource()
    {
        _manager.SetCurrentSource(1);

        Assert.Equal("laptop", _manager.CurrentSource);
    }

    [Fact]
    public void SetCurrentSource_ByIndex_RaisesIndexAndSourceChanged()
    {
        var indexHandler = new Mock<Action<int>>();
        var sourceHandler = new Mock<Action<string>>();
        _manager.OnSourceIndexChanged += indexHandler.Object;
        _manager.OnSourceChanged += sourceHandler.Object;

        _manager.SetCurrentSource(2);

        indexHandler.Verify(h => h.Invoke(2), Times.Once);
        sourceHandler.Verify(h => h.Invoke("wireless"), Times.Once);
    }

    [Fact]
    public void SetCurrentSource_ByIndex_SettingNonNoneSource_SetsPowerOn()
    {
        _manager.SetCurrentSource(1);

        Assert.Equal(PowerState.On, _manager.PowerState);
    }

    [Fact]
    public void SetCurrentSource_ByIndex_SettingNoneSource_SetsPowerOff()
    {
        _manager.SetCurrentSource(1);
        _manager.SetCurrentSource(0);

        Assert.Equal(PowerState.Off, _manager.PowerState);
    }

    [Fact]
    public void SetCurrentSource_ByName_DelegatesToIndexLookup()
    {
        _manager.SetCurrentSource("Wireless");

        Assert.Equal("wireless", _manager.CurrentSource);
    }

    [Fact]
    public void SetCurrentSource_ByUnknownName_DoesNotThrowAndDoesNotChangeState()
    {
        var initial = _manager.CurrentSource;

        _manager.SetCurrentSource("not-a-real-source");

        Assert.Equal(initial, _manager.CurrentSource);
    }

    [Fact]
    public void SetCurrentSource_OutOfRangeIndex_DoesNotThrow()
    {
        var initial = _manager.CurrentSource;

        _manager.SetCurrentSource(99);

        Assert.Equal(initial, _manager.CurrentSource);
    }

    [Fact]
    public void PowerOn_SelectsConfiguredDefaultSource()
    {
        _manager.PowerOn();

        Assert.Equal("laptop", _manager.CurrentSource);
        Assert.Equal(PowerState.On, _manager.PowerState);
    }

    [Fact]
    public void PowerOff_SelectsConfiguredOffSource()
    {
        _manager.PowerOn();

        _manager.PowerOff();

        Assert.Equal("None", _manager.CurrentSource);
        Assert.Equal(PowerState.Off, _manager.PowerState);
    }

    [Fact]
    public void SourceConnectionTransition_RaisesSourceListChanged()
    {
        var laptop = new TestSourceDefinition("Laptop", "Lectern HDMI", "laptop", "laptop");
        var manager = new SourceManager("test-room", [laptop]);
        var handler = new Mock<Action<List<SourceDefinition>>>();
        manager.OnSourceListChanged += handler.Object;

        laptop.SetIsConnected(true);

        handler.Verify(h => h.Invoke(It.Is<List<SourceDefinition>>(l => l.Contains(laptop))), Times.Once);
    }

    [Fact]
    public void SourceConnectionNoOp_DoesNotRaiseSourceListChanged()
    {
        var laptop = new TestSourceDefinition("Laptop", "Lectern HDMI", "laptop", "laptop");
        var manager = new SourceManager("test-room", [laptop]);
        var handler = new Mock<Action<List<SourceDefinition>>>();
        manager.OnSourceListChanged += handler.Object;

        laptop.SetIsConnected(false);

        handler.Verify(h => h.Invoke(It.IsAny<List<SourceDefinition>>()), Times.Never);
    }

    [Fact]
    public void SourcePreviewUrlNoOp_DoesNotRaiseSourceListChanged()
    {
        var laptop = new TestSourceDefinition("Laptop", "Lectern HDMI", "laptop", "laptop");
        var manager = new SourceManager("test-room", [laptop]);
        var handler = new Mock<Action<List<SourceDefinition>>>();
        manager.OnSourceListChanged += handler.Object;

        laptop.SetPreviewUrl(string.Empty);

        handler.Verify(h => h.Invoke(It.IsAny<List<SourceDefinition>>()), Times.Never);
    }
}
