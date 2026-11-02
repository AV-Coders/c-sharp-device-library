using System.Diagnostics;
using AVCoders.Core.Tests;

namespace AVCoders.MediaPlayer.Tests;

public class TriplePlayTest
{
    [Fact]
    public void SupportedButtons_AreEmpty()
    {
        Assert.Empty(new TriplePlay(1, "foo", "Name").SupportedButtons);
    }

    [Fact]
    public async Task PowerOn_UnderAnActivity_SendsNoTraceContextHeaders()
    {
        using var listener = new HeaderCapturingListener();
        var triplePlay = new TriplePlay(1, $"127.0.0.1:{listener.Port}", "Loopback");
        using var activity = new Activity("test").Start();
        activity.TraceStateString = "vendor=value";
        activity.AddBaggage("key", "value");

        var request = listener.NextRequestHeadersAsync();
        triplePlay.PowerOn();
        var headers = await request;

        Assert.All(TraceContext.Headers, header => Assert.Null(headers[header]));
    }

    [Fact]
    public void SetChannel_WithANegativeChannel_IsLogged()
    {
        var logs = LogCapture.Create(() => new TriplePlay(1, "foo", "Logged"), out var triplePlay);

        triplePlay.SetChannel(-1);

        Assert.Contains(logs.Logs, log => log.Message == "Channel -1 is invalid, it can't be negative");
    }

    [Fact]
    public void SetAudioMute_Unknown_IsLogged()
    {
        var logs = LogCapture.Create(() => new TriplePlay(1, "foo", "Logged"), out var triplePlay);

        triplePlay.SetAudioMute(AVCoders.Core.MuteState.Unknown);

        Assert.Contains(logs.Logs, log => log.Message == "Can't set the audio mute to Unknown");
    }
}
