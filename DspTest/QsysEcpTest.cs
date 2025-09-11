using AVCoders.Core;
using AVCoders.Core.Tests;

#pragma warning disable CS8602 // Dereference of a possibly null reference.

namespace AVCoders.Dsp.Tests;

public class QsysEcpTest
{
    private readonly QsysEcp _dsp;
    private readonly Mock<VolumeLevelHandler> _volumeLevelHandler = new();
    private readonly Mock<MuteStateHandler> _muteStateHandler = new();
    private readonly Mock<StringValueHandler> _stringValueHandler = new();
    private readonly Mock<CommunicationStateHandler> _communicationStateHandler = new();
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
    private const string GainName = "Gain";
    private const string MuteName = "Mute";
    private const string StringName = "String";

    public QsysEcpTest()
    {
        _dsp = new QsysEcp(_mockClient.Object, "Test", 100);

        _dsp.AddControl(_volumeLevelHandler.Object, GainName);
        _dsp.AddControl(_muteStateHandler.Object, MuteName);
        _dsp.AddControl(_stringValueHandler.Object, StringName);
        _dsp.CommunicationStateHandlers += _communicationStateHandler.Object;
    }

    [Fact]
    public void ControlNames_CanHaveSpaces()
    {
        var gainName = "Gain With Space";
        var muteName = "Mute with Space";
        var stringName = "String with Space";
        _dsp.AddControl(_volumeLevelHandler.Object, gainName);
        _dsp.AddControl(_muteStateHandler.Object, muteName);
        _dsp.AddControl(_stringValueHandler.Object, stringName);
        
        _dsp.SetLevel(gainName, 50);
        _mockClient.Verify(x => x.Send($"csp \"{gainName}\" 0.5\n"));
        
        _dsp.SetAudioMute(muteName, MuteState.On);
        _mockClient.Verify(x => x.Send($"css \"{muteName}\" muted\n"));
        
        _dsp.SetValue(stringName, "hello");
        _mockClient.Verify(x => x.Send($"css \"{stringName}\" hello\n"));
        
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{gainName}\" \"-6.40dB\" -6.4 0.989744");
        _volumeLevelHandler.Verify(x => x.Invoke(98));
        Assert.Equal(98, _dsp.GetLevel(gainName));
        
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{muteName}\" \"unmuted\" 1 1");
        _muteStateHandler.Verify(x => x.Invoke(MuteState.Off));
        Assert.Equal(MuteState.Off, _dsp.GetAudioMute(muteName));
        
        
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{StringName}\" \"This is a string\" 5 0.571429");

        Assert.Equal("This is a string", _dsp.GetValue(StringName));
    }

    [Fact]
    public void Constructor_SetsPortTo1702()
    {
        _mockClient.Verify(x => x.SetPort(1702), Times.Once);
    }

    [Fact]
    public void SetLevel_SendsTheCommand()
    {
        _dsp.SetLevel(GainName, 34);

        _mockClient.Verify(x => x.Send($"csp \"{GainName}\" 0.34\n"));
    }

    [Fact]
    public void LevelUp_IncrementsTheLevel()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{GainName}\" \"-6.40dB\" -6.4 0.919744");
        
        _dsp.LevelUp(GainName);
        
        _mockClient.Verify(x => x.Send($"csp \"{GainName}\" 0.92\n"));
    }

    [Fact]
    public void LevelUp_ObservesTheSpecifiedAmount()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{GainName}\" \"-6.40dB\" -6.4 0.919744");
        
        _dsp.LevelUp(GainName, 2);
        
        _mockClient.Verify(x => x.Send($"csp \"{GainName}\" 0.93\n"));
    }

    [Fact]
    public void LevelDown_DecrementsTheLevel()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{GainName}\" \"-6.40dB\" -6.4 0.919744");
        
        _dsp.LevelDown(GainName);
        
        _mockClient.Verify(x => x.Send($"csp \"{GainName}\" 0.9\n"));
    }

    [Fact]
    public void LevelDown_ObservesTheSpecifiedAmount()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{GainName}\" \"-6.40dB\" -6.4 0.919744");
        
        _dsp.LevelDown(GainName, 3);
        
        _mockClient.Verify(x => x.Send($"csp \"{GainName}\" 0.88\n"));
    }

    [Fact]
    public void GetLevel_IgnoresUnknownControlNames()
    {
        Assert.Equal(0, _dsp.GetLevel("foo"));
    }

    [Fact]
    public void GetLevel_ReturnsTheVolumePercentage()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{GainName}\" \"-6.40dB\" -6.4 0.919744");
        Assert.Equal(91, _dsp.GetLevel(GainName));
    }

    [Fact]
    public void SetAudioMute_SendsTheCommand()
    {
        _dsp.SetAudioMute(MuteName, MuteState.On);
        _mockClient.Verify(x => x.Send($"css \"{MuteName}\" muted\n"));
    }

    [Fact]
    public void GetAudioMute_IgnoresUnknownControlNames()
    {
        Assert.Equal(MuteState.Unknown, _dsp.GetAudioMute("foo"));
    }

    [Fact]
    public void GetAudioMute_ReturnsTheMuteState()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{MuteName}\" \"muted\" 1 1");

        Assert.Equal(MuteState.On, _dsp.GetAudioMute(MuteName));
    }

    [Fact]
    public void ToggleAudioMute_SendsTheCommand()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{MuteName}\" \"unmuted\" 1 1");

        _dsp.ToggleAudioMute(MuteName);

        _mockClient.Verify(x => x.Send($"css \"{MuteName}\" muted\n"));
    }

    [Fact]
    public void SetValue_SendsTheCommand()
    {
        _dsp.SetValue(StringName, "hello");
        _mockClient.Verify(x => x.Send($"css \"{StringName}\" hello\n"));
    }

    [Fact]
    public void GetValue_IgnoresUnknownControlNames()
    {
        Assert.Equal("", _dsp.GetValue("foo"));
    }

    [Fact]
    public void GetValue_ReturnsTheValue()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{StringName}\" \"5\" 5 0.571429");

        Assert.Equal("5", _dsp.GetValue(StringName));
    }

    [Fact]
    public void HandleResponse_GivenAFaderValue_UpdatesTheFaderLevel()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{GainName}\" \"-6.40dB\" -6.4 0.989744");

        Assert.Equal(98, _dsp.GetLevel(GainName));
    }

    [Fact]
    public void HandleResponse_GivenAFaderValue_CallsTheDelegate()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{GainName}\" \"-6.40dB\" -6.4 0.989744");

        _volumeLevelHandler.Verify(x => x.Invoke(98));
    }

    [Theory]
    [InlineData("unmuted")]
    [InlineData("false")]
    public void HandleResponse_GivenAMuteValue_UpdatesTheMuteState(string muteState)
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{MuteName}\" \"{muteState}\" 1 1");

        Assert.Equal(MuteState.Off, _dsp.GetAudioMute(MuteName));
    }
    
    [Theory]
    [InlineData("muted")]
    [InlineData("true")]
    public void HandleResponse_GivenAMuteValue_UpdatesTheMuteStateForMuted(string muteState)
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{MuteName}\" \"{muteState}\" 1 1");

        Assert.Equal(MuteState.On, _dsp.GetAudioMute(MuteName));
    }

    [Fact]
    public void HandleResponse_GivenAMuteValue_CallsTheDelegate()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{MuteName}\" \"unmuted\" 1 1");

        _muteStateHandler.Verify(x => x.Invoke(MuteState.Off));
    }

    [Fact]
    public void HandleResponse_GivenAStringValue_UpdatesTheValue()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{StringName}\" \"This is a string\" 5 0.571429");

        Assert.Equal("This is a string", _dsp.GetValue(StringName));
    }

    [Fact]
    public void HandleResponse_GivenAStringValue_CallsTheDelegate()
    {
        _mockClient.Object.ResponseHandlers.Invoke($"cv \"{StringName}\" \"5\" 5 0.571429");

        _stringValueHandler.Verify(x => x.Invoke("5"));
    }

    [Fact]
    public void HandleResponse_GivenAnInvalidResponse_DoesNothing()
    {
        _mockClient.Object.ResponseHandlers.Invoke("cv \"gai ");
    }

    [Fact]
    public void HandleResponse_GivenABadResponse_ReportsError()
    {
        _mockClient.Object.ResponseHandlers.Invoke("cv \"Zone33BGMMute\"");
        _mockClient.Object.ResponseHandlers.Invoke("bad_id \"Zone33BGMMute\"");
        
        Assert.Equal(CommunicationState.Error, _dsp.CommunicationState);
        _communicationStateHandler.Verify(x => x.Invoke(CommunicationState.Error));
    }

    [Fact]
    public void HandleResponse_GivenABatchResponse_ParsesEverythingCorrectly()
    {
        _dsp.AddControl(_stringValueHandler.Object, "Zone30BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone10BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone17BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone18BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone1BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone2BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone3BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone4BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone5BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone6BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone7BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone9BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone11BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone12BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone13BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone14BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone15BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone19BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone20BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone21BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone22BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone23BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone24BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone25BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone26BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone27BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone28BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone29BGMSelect");
        _dsp.AddControl(_stringValueHandler.Object, "Zone31BGMSelect");
        var response =
            "cv \"Zone30BGMSelect\" \"7\" 7 0.857143\ncv \"Zone10BGMSelect\" \"3\" 3 0.285714\ncv \"Zone17BGMSelect\" \"4\" 4 0.428571\ncv \"Zone18BGMSelect\" \"1\" 1 0\ncv \"Zone1BGMSelect\" \"1\" 1 0\ncv \"Zone2BGMSelect\" \"2\" 2 0.142857\ncv \"Zone3BGMSelect\" \"3\" 3 0.285714\ncv \"Zone4BGMSelect\" \"4\" 4 0.428571\ncv \"Zone5BGMSelect\" \"6\" 6 0.714286\ncv \"Zone6BGMSelect\" \"4\" 4 0.428571\ncv \"Zone7BGMSelect\" \"4\" 4 0.428571\ncv \"Zone9BGMSelect\" \"2\" 2 0.142857\ncv \"Zone11BGMSelect\" \"1\" 1 0\ncv \"Zone12BGMSelect\" \"2\" 2 0.142857\ncv \"Zone13BGMSelect\" \"1\" 1 0\ncv \"Zone14BGMSelect\" \"2\" 2 0.142857\ncv \"Zone15BGMSelect\" \"3\" 3 0.285714\ncv \"Zone19BGMSelect\" \"6\" 6 0.714286\ncv \"Zone20BGMSelect\" \"4\" 4 0.428571\ncv \"Zone21BGMSelect\" \"3\" 3 0.285714\ncv \"Zone22BGMSelect\" \"2\" 2 0.142857\ncv \"Zone23BGMSelect\" \"1\" 1 0\ncv \"Zone24BGMSelect\" \"6\" 6 0.714286\ncv \"Zone25BGMSelect\" \"4\" 4 0.428571\ncv \"Zone26BGMSelect\" \"4\" 4 0.428571\ncv \"Zone27BGMSelect\" \"4\" 4 0.428571\ncv \"Zone28BGMSelect\" \"4\" 4 0.428571\ncv \"Zone29BGMSelect\" \"4\" 4 0.428571\ncv \"Zone31BGMSelect\" \"6\" 6 0.71428\n";
        _mockClient.Object.ResponseHandlers.Invoke(response);
        
        _stringValueHandler.Verify(x => x.Invoke("7"), Times.Once);
        _stringValueHandler.Verify(x => x.Invoke("3"), Times.Exactly(4));
        _stringValueHandler.Verify(x => x.Invoke("1"), Times.Exactly(5));
    }
}