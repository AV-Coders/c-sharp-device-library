using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Display.Tests;

public class DummyDisplay : Display
{
    private readonly CommunicationClient _client;
    public DummyDisplay(CommunicationClient client, List<Input> supportedInputs) 
        : base(supportedInputs, "dummy", Input.Hdmi1, client, CommandStringFormat.Unknown) { _client = client; }
    protected override Task DoPoll(CancellationToken token) => PollWorker.Stop();

    protected override void DoPowerOn() => _client.Send("Power On");
    protected override void DoPowerOff() => _client.Send("Power Off");
    protected override void DoSetInput(Input input) => _client.Send($"Input {input.ToString()}");
    protected override void DoSetVolume(int volume) => _client.Send($"Volume {volume}");
    protected override void DoSetAudioMute(MuteState state) => _client.Send($"Mute {state.ToString()}");
    protected override void HandleConnectionState(ConnectionState connectionState) { }

    public void PowerOffResponse()
    {
        PowerState = PowerState.Off;
        ProcessPowerResponse();
    }

    public void PowerOnResponse()
    {
        PowerState = PowerState.On;
        ProcessPowerResponse();
    }

    public void InputHdmi2Response()
    {
        Input = Input.Hdmi2;
        ProcessInputResponse();
    }

    public void InputHdmi1Response()
    {
        Input = Input.Hdmi1;
        ProcessInputResponse();
    }

    public void CommunicationErrorResponse() => CommunicationState = CommunicationState.Error;

    public void VideoMuteResponse(MuteState state) => VideoMute = state;
}

public class DisplayTest
{
    private readonly DummyDisplay _display;
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();

    public DisplayTest()
    {
        _display = new DummyDisplay(_mockClient.Object, [Input.Hdmi1]);
    }

    private class TestClient() : CommunicationClient("Test", "10.0.0.1", 4999, CommandStringFormat.Ascii)
    {
        public override void Send(string message) { }

        public override void Send(byte[] bytes) { }

        public void Drop() => ConnectionState = ConnectionState.Disconnected;

        public void Disconnect()
        {
            DesiredConnectionState = ConnectionState.Disconnected;
            ConnectionState = ConnectionState.Disconnected;
        }
    }

    [Fact]
    public void RequestedDisconnect_SetsNotAttempted_AndResolvesTheCommunicationIssue()
    {
        var client = new TestClient();
        var display = new DummyDisplay(client, [Input.Hdmi1]);
        display.CommunicationErrorResponse();

        client.Disconnect();

        Assert.Equal(CommunicationState.NotAttempted, display.CommunicationState);
        Assert.Empty(display.GetOngoingIssues());
    }

    [Fact]
    public void DroppedConnection_LeavesTheCommunicationStateAlone()
    {
        var client = new TestClient();
        var display = new DummyDisplay(client, [Input.Hdmi1]);
        display.CommunicationErrorResponse();

        client.Drop();

        Assert.Equal(CommunicationState.Error, display.CommunicationState);
    }

    [Fact]
    public void ProcessPowerResponse_ForcesThePowerState()
    {
        _display.PowerOn();

        _display.PowerOffResponse();

        _mockClient.Verify(x => x.Send("Power On"), Times.Exactly(2));
    }

    [Fact]
    public void ProcessPowerResponse_ForcingPowerOn_KeepsTheDesiredInput()
    {
        var display = new DummyDisplay(_mockClient.Object, [Input.Hdmi1, Input.Hdmi2]);
        display.PowerOn();
        display.SetInput(Input.Hdmi2);

        display.PowerOffResponse();

        _mockClient.Verify(x => x.Send("Power On"), Times.Exactly(2));
        Assert.Equal(Input.Hdmi2, display.DesiredInput);
    }

    [Fact]
    public void ProcessInputResponse_ForcesTheInput()
    {
        _display.SetInput(Input.Hdmi1);
        
        _display.InputHdmi2Response();
        
        _mockClient.Verify(x => x.Send("Input Hdmi1"), Times.Exactly(2));
    }

    [Fact]
    public void ProcessPowerResponse_RaisesAnOngoingIssue_UntilThePowerStateRecovers()
    {
        _display.PowerOn();

        _display.PowerOffResponse();
        var issue = Assert.Single(_display.GetOngoingIssues(), i => i.Key == "power-state");
        Assert.Equal(IssueStatus.Ongoing, issue.Status);

        _display.PowerOnResponse();
        Assert.DoesNotContain(_display.GetOngoingIssues(), i => i.Key == "power-state");
        Assert.Contains(_display.GetIssues(), i => i.Key == "power-state" && i.Status == IssueStatus.Resolved);
    }

    [Fact]
    public void ProcessInputResponse_RaisesAnOngoingIssue_UntilTheInputRecovers()
    {
        var changedCount = 0;
        _display.IssuesChanged += (_, _) => changedCount++;
        _display.SetInput(Input.Hdmi1);

        _display.InputHdmi2Response();
        Assert.Contains(_display.GetOngoingIssues(), i => i.Key == "input");
        Assert.Equal(1, changedCount);

        _display.InputHdmi1Response();
        Assert.DoesNotContain(_display.GetOngoingIssues(), i => i.Key == "input");
        Assert.Equal(2, changedCount);
    }

    [Fact]
    public void PowerOff_UpdatesInternalPowerState()
    {
        _display.PowerOff();

        Assert.Equal(PowerState.Off, _display.PowerState);
    }

    [Fact]
    public void PowerOn_UpdatesInternalPowerState()
    {
        _display.PowerOn();

        Assert.Equal(PowerState.On, _display.PowerState);
    }
    

    [Fact]
    public void SetInput_IgnoresMissingInputs()
    {
        _display.SetInput(Input.Hdmi2);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void SetVolume_CallsTheDelegate(int volume)
    {
        Mock<VolumeLevelHandler> volumeLevelHandler = new Mock<VolumeLevelHandler>();
        _display.VolumeLevelHandlers += volumeLevelHandler.Object;
        _display.SetVolume(volume);
        
        volumeLevelHandler.Verify(x => x.Invoke(volume));
    }

    [Fact]
    public void LevelUp_GivenANegativeAmount_StopsAtZero()
    {
        _display.SetVolume(10);

        _display.LevelUp(-50);

        Assert.Equal(0, _display.Volume);
        _mockClient.Verify(x => x.Send("Volume 0"), Times.Once);
    }

    [Fact]
    public void LevelDown_GivenANegativeAmount_StopsAt100()
    {
        _display.SetVolume(90);

        _display.LevelDown(-50);

        Assert.Equal(100, _display.Volume);
        _mockClient.Verify(x => x.Send("Volume 100"), Times.Once);
    }

    [Theory]
    [InlineData(MuteState.On)]
    [InlineData(MuteState.Off)]
    public void setAudioMute_CallsTheDelegate(MuteState state)
    {
        Mock<MuteStateHandler> muteStateHandler = new Mock<MuteStateHandler>();
        _display.MuteStateHandlers = muteStateHandler.Object;
        _display.SetAudioMute(state);

        muteStateHandler.Verify(x => x.Invoke(state));
    }

    [Fact]
    public void setAudioMute_UpdatesInternalState()
    {
        _display.SetAudioMute(MuteState.On);

        Assert.Equal(MuteState.On, _display.AudioMute);
    }

    [Fact]
    public void SetAudioMute_GivenUnknown_SendsNothingAndKeepsTheMuteState()
    {
        _display.SetAudioMute(MuteState.On);

        _display.SetAudioMute(MuteState.Unknown);

        Assert.Equal(MuteState.On, _display.AudioMute);
        _mockClient.Verify(x => x.Send("Mute Unknown"), Times.Never);
        Assert.Contains(_display.Events, e => e.Type == EventType.Error && e.Info == "Audio mute needs to be On or Off, it's Unknown");
    }

    [Fact]
    public void VideoMute_CallsTheDelegateOncePerChange()
    {
        Mock<MuteStateHandler> videoMuteHandler = new Mock<MuteStateHandler>();
        _display.VideoMuteHandlers += videoMuteHandler.Object;

        _display.VideoMuteResponse(MuteState.On);
        _display.VideoMuteResponse(MuteState.On);

        videoMuteHandler.Verify(x => x.Invoke(MuteState.On), Times.Once);
        Assert.Equal(MuteState.On, _display.VideoMute);
        Assert.Contains(_display.Events, e => e.Type == EventType.VideoMute && e.Info == "On");
    }
}