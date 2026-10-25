using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;

namespace AVCoders.Dsp.Tests;

[Collection(LoggerFactoryCollection.Name)]
public class BoseCspSoIPTest
{
    private readonly BoseCspSoIP _dsp;
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
    private readonly Mock<VolumeLevelHandler> _volumeLevelHandler = new();
    private readonly Mock<MuteStateHandler> _muteStateHandler = new();
    private readonly Mock<StringValueHandler> _stringValueHandler = new();

    private const string GainName = "Fitness";
    private const string MuteName = "Fitness";
    private const string StringName = "Source";
    private const string UnknownControl = "Nowhere";

    public BoseCspSoIPTest()
    {
        // A long poll interval so the background poll worker never runs during a test.
        _dsp = new BoseCspSoIP(_mockClient.Object, "Test", 500000);

        _mockClient.Setup(x => x.Send($"GA\"{GainName} Gain\">1\r"))
            .Callback(() => _mockClient.Object.ResponseHandlers!.Invoke($"GA\"{GainName} Gain\">1=0\r"));
        _mockClient.Setup(x => x.Send($"GA\"{MuteName} Gain\">2\r"))
            .Callback(() => _mockClient.Object.ResponseHandlers!.Invoke($"GA\"{MuteName} Gain\">2=F\r"));
        _mockClient.Setup(x => x.Send($"GA\"{StringName} Selector\">1\r"))
            .Callback(() => _mockClient.Object.ResponseHandlers!.Invoke($"GA\"{StringName} Selector\">1=2\r"));

        _dsp.AddControl(_volumeLevelHandler.Object, GainName);
        _dsp.AddControl(_muteStateHandler.Object, MuteName);
        _dsp.AddControl(_stringValueHandler.Object, StringName);
    }

    [Fact]
    public void HandleResponse_StoresThePercentage()
    {
        _mockClient.Object.ResponseHandlers!.Invoke($"GA\"{GainName} Gain\">1=12\r");

        Assert.Equal(100, _dsp.GetLevel(GainName));
    }

    [Theory]
    [InlineData($"GA\"{MuteName} Gain\">2=F\r", MuteState.Off)]
    [InlineData($"GA\"{MuteName} Gain\">2=O\r", MuteState.On)]
    public void HandleResponse_StoresTheMuteState(string response, MuteState expectedMuteState)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);

        Assert.Equal(expectedMuteState, _dsp.GetAudioMute(MuteName));
    }

    [Fact]
    public void HandleResponse_StoresTheStringValue()
    {
        _mockClient.Object.ResponseHandlers!.Invoke($"GA\"{StringName} Selector\">1=3\r");

        Assert.Equal("3", _dsp.GetValue(StringName));
    }

    [Fact]
    public void HandleResponse_IgnoresUnknownControls()
    {
        var levelBefore = _dsp.GetLevel(GainName);

        _mockClient.Object.ResponseHandlers!.Invoke("GA\"Somewhere else Gain\">1=12\r");

        Assert.Equal(levelBefore, _dsp.GetLevel(GainName));
    }

    [Theory]
    [InlineData(0, $"SA\"{GainName} Gain\">1=-60.5\r")]
    [InlineData(100, $"SA\"{GainName} Gain\">1=12\r")]
    [InlineData(50, $"SA\"{GainName} Gain\">1=-24\r")]
    [InlineData(13, $"SA\"{GainName} Gain\">1=-51\r")]
    public void SetLevel_SendsTheCorrectDB(int percentage, string expectedCommand)
    {
        _dsp.SetLevel(GainName, percentage);
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Theory]
    [InlineData("1", $"SA\"{GainName} Selector\">1=1\r")]
    [InlineData("2", $"SA\"{GainName} Selector\">1=2\r")]
    [InlineData("3", $"SA\"{GainName} Selector\">1=3\r")]
    [InlineData("4", $"SA\"{GainName} Selector\">1=4\r")]
    public void SetValue_SendsTheCommand(string source, string expectedCommand)
    {
        _dsp.SetValue(GainName, source);
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Fact]
    public void HandleResponse_UpdatesTheCommunicationState()
    {
        _mockClient.Object.ResponseHandlers!.Invoke($"GA\"{GainName} Gain\">1=12\r");

        Assert.Equal(CommunicationState.Okay, _dsp.CommunicationState);
    }

    [Fact]
    public async Task Poll_QueriesEachControlWithItsFullDeviceName()
    {
        var connectionStateField = typeof(CommunicationClient)
            .GetField("_connectionState", BindingFlags.Instance | BindingFlags.NonPublic);
        connectionStateField!.SetValue(_mockClient.Object, ConnectionState.Connected);

        var poll = typeof(BoseCspSoIP).GetMethod("Poll", BindingFlags.Instance | BindingFlags.NonPublic);
        await (Task)poll!.Invoke(_dsp, [CancellationToken.None])!;

        _mockClient.Verify(x => x.Send($"GA\"{GainName} Gain\">1\r"), Times.Once);
        _mockClient.Verify(x => x.Send($"GA\"{MuteName} Gain\">2\r"), Times.Once);
        _mockClient.Verify(x => x.Send($"GA\"{StringName} Selector\">1\r"), Times.Once);
    }

    [Fact]
    public void AddControl_WhileDisconnected_SendsNoQuery()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Disconnected);
        var dsp = new BoseCspSoIP(client, "Test", 500000);

        RegisterOneOfEachControl(dsp);

        Assert.Empty(client.Sent);
    }

    [Fact]
    public void AddControl_WhileConnected_QueriesTheControlStraightAway()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = new BoseCspSoIP(client, "Test", 500000);

        RegisterOneOfEachControl(dsp);

        Assert.Contains($"GA\"{GainName} Gain\">1\r", client.Sent);
        Assert.Contains($"GA\"{MuteName} Gain\">2\r", client.Sent);
        Assert.Contains($"GA\"{StringName} Selector\">1\r", client.Sent);
    }

    [Fact]
    public async Task Connecting_QueriesEachControl()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Disconnected);
        var dsp = new BoseCspSoIP(client, "Test", 500000);
        RegisterOneOfEachControl(dsp);

        client.SetConnectionState(ConnectionState.Connected);

        await Wait.UntilAsync(() => client.Sent.Contains($"GA\"{StringName} Selector\">1\r"), "the selector query after connecting");
        Assert.Contains($"GA\"{GainName} Gain\">1\r", client.Sent);
        Assert.Contains($"GA\"{MuteName} Gain\">2\r", client.Sent);
    }

    [Fact]
    public void HandleConnectionState_GivenADrop_ReportsUnknown()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = new BoseCspSoIP(client, "Test", 500000);
        client.ResponseHandlers!.Invoke($"GA\"{GainName} Gain\">1=12\r");
        Assert.Equal(CommunicationState.Okay, dsp.CommunicationState);

        client.SetConnectionState(ConnectionState.Disconnected);

        Assert.Equal(CommunicationState.Unknown, dsp.CommunicationState);
    }

    [Fact]
    public void ClientDisconnectedOnRequest_ReportsNotAttempted()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = new BoseCspSoIP(client, "Test", 500000);
        client.ResponseHandlers!.Invoke($"GA\"{GainName} Gain\">1=12\r");

        client.Disconnect();

        Assert.Equal(CommunicationState.NotAttempted, dsp.CommunicationState);
    }

    [Theory]
    [InlineData(1, $"SA\"{GainName} Gain\">3=-1\r")]
    [InlineData(-2, $"SA\"{GainName} Gain\">3=2\r")]
    public void LevelDown_SendsASignedStep(int amount, string expectedCommand)
    {
        _dsp.LevelDown(GainName, amount);

        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Fact]
    public void AddControl_ToAnExistingGain_AppliesTheNewRange()
    {
        _dsp.AddControl(_volumeLevelHandler.Object, GainName, -80, 10);

        _dsp.SetLevel(GainName, 0);
        _dsp.SetLevel(GainName, 100);

        _mockClient.Verify(x => x.Send($"SA\"{GainName} Gain\">1=-80\r"));
        _mockClient.Verify(x => x.Send($"SA\"{GainName} Gain\">1=10\r"));
    }

    [Fact]
    public void AddControl_WithTheDefaultRangeAfterARangedCall_KeepsTheRange()
    {
        _dsp.AddControl(_volumeLevelHandler.Object, "Main", -40, 0);
        _ = new BoseCspSoIPVolumeControl(new BoseAudioBlockInfo("Main", "Main"), VolumeType.Speaker, _dsp);

        _dsp.SetLevel("Main", 0);
        _dsp.SetLevel("Main", 100);

        _mockClient.Verify(x => x.Send("SA\"Main Gain\">1=-40\r"));
        _mockClient.Verify(x => x.Send("SA\"Main Gain\">1=0\r"));
    }

    [Fact]
    public async Task Poll_WhileAControlIsAdded_QueriesTheControlsItStartedWith()
    {
        var client = new TestTcpClient();
        var dsp = new BoseCspSoIP(client, "Test", 500000);
        RegisterOneOfEachControl(dsp);
        typeof(CommunicationClient).GetField("_connectionState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, ConnectionState.Connected);
        var poll = (Task)typeof(BoseCspSoIP).GetMethod("Poll", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dsp, [CancellationToken.None])!;

        dsp.AddControl(_volumeLevelHandler.Object, "Late");
        await poll;

        Assert.Contains($"GA\"{GainName} Gain\">1\r", client.Sent);
        Assert.Contains($"GA\"{MuteName} Gain\">2\r", client.Sent);
        Assert.Contains($"GA\"{StringName} Selector\">1\r", client.Sent);
    }

    [Fact]
    public void SetLevel_GivenAnUnknownControl_SendsNothingAndLogs()
    {
        var logs = new ScopeCapturingLoggerFactory();
        var client = new TestTcpClient();
        var dsp = CreateLogged(logs, () => new BoseCspSoIP(client, "Test", 500000));

        dsp.SetLevel(UnknownControl, 40);

        Assert.Empty(client.Sent);
        Assert.Contains(logs.Logs, x => x.Message == $"Unknown level {UnknownControl}, not setting it to 40");
    }

    [Fact]
    public void Gets_GivenAnUnknownControl_ReturnTheDefaultAndLog()
    {
        var logs = new ScopeCapturingLoggerFactory();
        var dsp = CreateLogged(logs, () => new BoseCspSoIP(new TestTcpClient(), "Test", 500000));

        Assert.Equal(0, dsp.GetLevel(UnknownControl));
        Assert.Equal(MuteState.Unknown, dsp.GetAudioMute(UnknownControl));
        Assert.Equal("", dsp.GetValue(UnknownControl));
        Assert.Contains(logs.Logs, x => x.Message == $"Unknown level {UnknownControl}, returning 0");
        Assert.Contains(logs.Logs, x => x.Message == $"Unknown mute {UnknownControl}, returning Unknown");
        Assert.Contains(logs.Logs, x => x.Message == $"Unknown selector {UnknownControl}, returning an empty value");
    }

    private void RegisterOneOfEachControl(BoseCspSoIP dsp)
    {
        dsp.AddControl(_volumeLevelHandler.Object, GainName);
        dsp.AddControl(_muteStateHandler.Object, MuteName);
        dsp.AddControl(_stringValueHandler.Object, StringName);
    }

    private static T CreateLogged<T>(ScopeCapturingLoggerFactory logs, Func<T> create)
    {
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = logs;
        try
        {
            return create();
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }
}
