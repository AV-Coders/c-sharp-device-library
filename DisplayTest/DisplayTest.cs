using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Display.Tests;

public class DummyDisplay : Display
{
    private readonly TcpClient _client;
    public DummyDisplay(TcpClient client, List<Input> supportedInputs) 
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
}

public class DisplayTest
{
    private readonly DummyDisplay _display;
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();

    public DisplayTest()
    {
        _display = new DummyDisplay(_mockClient.Object, [Input.Hdmi1]);
    }

    [Fact]
    public void ProcessPowerResponse_ForcesThePowerState()
    {
        _display.PowerOn();

        _display.PowerOffResponse();

        _mockClient.Verify(x => x.Send("Power On"), Times.Exactly(2));
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
        var issue = Assert.Single(_display.OngoingIssues, i => i.Key == "power-state");
        Assert.Equal(IssueStatus.Ongoing, issue.Status);

        _display.PowerOnResponse();
        Assert.DoesNotContain(_display.OngoingIssues, i => i.Key == "power-state");
        Assert.Contains(_display.Issues, i => i.Key == "power-state" && i.Status == IssueStatus.Resolved);
    }

    [Fact]
    public void ProcessInputResponse_RaisesAnOngoingIssue_UntilTheInputRecovers()
    {
        var changedCount = 0;
        _display.IssuesChanged += (_, _) => changedCount++;
        _display.SetInput(Input.Hdmi1);

        _display.InputHdmi2Response();
        Assert.Contains(_display.OngoingIssues, i => i.Key == "input");
        Assert.Equal(1, changedCount);

        _display.InputHdmi1Response();
        Assert.DoesNotContain(_display.OngoingIssues, i => i.Key == "input");
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
}