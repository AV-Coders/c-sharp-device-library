using System.Reflection;
using AVCoders.Core;
using Moq;

namespace AVCoders.Display.Tests;

public class SonyRestTest
{
    private readonly Mock<RestComms> _restComms = new("host", (ushort)80, "test");
    private readonly SonyRest _display;

    public SonyRestTest()
    {
        _display = new SonyRest("Test display", Input.Hdmi1, _restComms.Object, "secret");
    }

    [Fact]
    public void SetAudioMute_Unknown_SendsNothingAndKeepsTheMuteState()
    {
        _display.SetAudioMute(MuteState.On);
        _restComms.Invocations.Clear();

        _display.SetAudioMute(MuteState.Unknown);

        _restComms.Verify(x => x.Post(It.IsAny<Uri?>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        Assert.Equal(MuteState.On, _display.AudioMute);
        Assert.Contains(_display.Events, e => e.Type == EventType.Error);
    }

    [Fact]
    public void RestartPolling_LeavesThePollWorkerStopped()
    {
        typeof(Display).GetMethod("RestartPolling", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_display, null);

        var pollWorker = (ThreadWorker)typeof(Display).GetField("PollWorker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_display)!;
        Assert.False(pollWorker.IsRunning);
    }
}
