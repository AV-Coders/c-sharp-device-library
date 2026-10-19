using System.Diagnostics;
using AVCoders.Core.Tests;

namespace AVCoders.MediaPlayer.Tests;

public class TriplePlayTest
{
    private static readonly RemoteButton[] _excludedButtons = Enum.GetValues<RemoteButton>();
    private readonly TriplePlay _triplePlay = new(1, "foo", "Name");

    [Fact]
    public void SupportedButtons_AreExactlyTheButtonsTheTestExpects()
    {
        Assert.Equal(Enum.GetValues<RemoteButton>().Except(_excludedButtons).OrderBy(b => b), _triplePlay.SupportedButtons.OrderBy(b => b));
        Assert.Empty(_triplePlay.SupportedButtons);
    }

    public static IEnumerable<object[]> ExcludedButtonValues() => _excludedButtons.Select(rb => new object[] { rb });

    [Theory]
    [MemberData(nameof(ExcludedButtonValues))]
    public void SendIRCode_ExcludedButtonsAreNotSupported(RemoteButton button)
    {
        _triplePlay.SendIRCode(button);

        Assert.DoesNotContain(button, _triplePlay.SupportedButtons);
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
}
