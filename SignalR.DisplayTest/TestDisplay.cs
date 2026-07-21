using AVCoders.Core;
using AVCoders.Display;
using AVCoders.Core.Tests;

namespace AVCoders.SignalR.Display.Tests;

/// <summary>
/// Concrete <see cref="AVCoders.Display.Display"/> used by the SignalR.Display tests so we can
/// drive state changes from outside the class and observe command invocations.
/// </summary>
public class TestDisplay : AVCoders.Display.Display
{
    public int DoPowerOnCallCount;
    public int DoPowerOffCallCount;
    public Input? LastDoSetInputArg;
    public int? LastDoSetVolumeArg;
    public MuteState? LastDoSetAudioMute;

    public TestDisplay(string name = "TestDisplay", List<Input>? supportedInputs = null)
        : base(supportedInputs ?? new List<Input> { Input.Hdmi1, Input.Hdmi2 },
            name,
            null,
            TestFactory.CreateCommunicationClient().Object,
            CommandStringFormat.Ascii)
    {
    }

    public void SetPowerStateForTest(PowerState state) => PowerState = state;
    public void SetInputForTest(Input input) => Input = input;
    public void SetVolumeForTest(int volume) => Volume = volume;
    public void SetAudioMuteForTest(MuteState state) => MuteState = state;

    protected override void DoPowerOn() => DoPowerOnCallCount++;
    protected override void DoPowerOff() => DoPowerOffCallCount++;
    protected override void DoSetInput(Input input) => LastDoSetInputArg = input;
    protected override void DoSetVolume(int percentage) => LastDoSetVolumeArg = percentage;
    protected override void DoSetAudioMute(MuteState state) => LastDoSetAudioMute = state;
    protected override Task DoPoll(CancellationToken token) => Task.CompletedTask;
    protected override void HandleConnectionState(ConnectionState connectionState) { }
}
