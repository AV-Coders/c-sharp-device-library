using AVCoders.Core;
using AVCoders.Core.Tests;
using AVCoders.MediaPlayer;

namespace AVCoders.SignalR.SetTopBox.Tests;

public class TestSetTopBox : AVCoders.MediaPlayer.MediaPlayer, ISetTopBox
{
    public static readonly RemoteButton[] DefaultSupportedButtons =
        [RemoteButton.Up, RemoteButton.Down, RemoteButton.Enter, RemoteButton.Play, RemoteButton.PowerOn, RemoteButton.PowerOff];

    public int PowerOnCallCount;
    public int PowerOffCallCount;
    public readonly List<RemoteButton> SentButtons = [];

    public TestSetTopBox(string name = "TestSetTopBox", IReadOnlyCollection<RemoteButton>? supportedButtons = null)
        : base(name, TestFactory.CreateCommunicationClient().Object)
    {
        SupportedButtons = supportedButtons ?? DefaultSupportedButtons;
    }

    public IReadOnlyCollection<RemoteButton> SupportedButtons { get; }

    public void SetPowerStateForTest(PowerState state) => PowerState = state;
    public void SetDesiredPowerStateForTest(PowerState state) => DesiredPowerState = state;
    public void SetCommunicationStateForTest(CommunicationState state) => CommunicationState = state;

    public override void PowerOn() => PowerOnCallCount++;
    public override void PowerOff() => PowerOffCallCount++;
    public void SendIRCode(RemoteButton button) => SentButtons.Add(button);
    public void ChannelUp() { }
    public void ChannelDown() { }
    public void SetChannel(int channel) { }
    public void ToggleSubtitles() { }
}

public class TestPlainMediaPlayer : AVCoders.MediaPlayer.MediaPlayer
{
    public TestPlainMediaPlayer(string name = "TestPlainMediaPlayer")
        : base(name, TestFactory.CreateCommunicationClient().Object)
    {
    }

    public override void PowerOn() { }
    public override void PowerOff() { }
}
