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
}
