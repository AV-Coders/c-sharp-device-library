using AVCoders.Core;
using Moq;

namespace AVCoders.Display.Tests;

public class DummyDisplay : Display
{
    private readonly TcpClient _client;
    public DummyDisplay(TcpClient client, List<Input> supportedInputs) : base(supportedInputs) { _client = client; }
    protected override void Poll() { PollWorker.Stop(); }

    protected override void DoPowerOn() => _client.Send("Power On");
    protected override void DoPowerOff() => _client.Send("Power Off");
    protected override void DoSetInput(Input input) => _client.Send($"Input {input.ToString()}");
    protected override void DoSetVolume(int volume) => _client.Send($"Volume {volume}");
    protected override void DoSetAudioMute(MuteState state) => _client.Send($"Mute {state.ToString()}");

    public void InvokeLog(string message) => Log(message);
    public void PowerOffResponse()
    {
        PowerState = PowerState.Off;
        ProcessPowerResponse();
    }

    public void InputHdmi2Response()
    {
        Input = Input.Hdmi2;
        ProcessInputResponse();
    }
}

public class DisplayTest
{
    private readonly DummyDisplay _dummyDisplay;
    private readonly Mock<TcpClient> _mockClient;
    private readonly Mock<LogHandler> _mockLogHandler;

    public DisplayTest()
    {
        _mockClient = new Mock<TcpClient>("foo", (ushort) 1);
        _mockLogHandler = new Mock<LogHandler>();
        _dummyDisplay = new DummyDisplay(_mockClient.Object, new List<Input> { Input.Hdmi1 });
        _dummyDisplay.LogHandlers += _mockLogHandler.Object;
    }

    [Fact]
    public void ProcessPowerResponse_ForcesThePowerState()
    {
        _dummyDisplay.PowerOn();

        _dummyDisplay.PowerOffResponse();

        _mockClient.Verify(x => x.Send("Power On"), Times.Exactly(2));
        
        _mockLogHandler.Verify(x => x.Invoke("AVCoders.Display.Tests.DummyDisplay - Turning On", EventLevel.Verbose), Times.Exactly(2));
    }

    [Fact]
    public void ProcessInputResponse_ForcesTheInput()
    {
        _dummyDisplay.SetInput(Input.Hdmi1);
        
        _dummyDisplay.InputHdmi2Response();
        
        _mockClient.Verify(x => x.Send("Input Hdmi1"), Times.Exactly(2));
        
        _mockLogHandler.Verify(x => x.Invoke("AVCoders.Display.Tests.DummyDisplay - Setting input to Hdmi1", EventLevel.Verbose), Times.Exactly(2));
    }

    [Fact]
    public void Logging_UsesTheCorrectClassName()
    {
        _dummyDisplay.InvokeLog("Hello");
        
        _mockLogHandler.Verify(x => x.Invoke("AVCoders.Display.Tests.DummyDisplay - Hello", EventLevel.Verbose));
    }
}