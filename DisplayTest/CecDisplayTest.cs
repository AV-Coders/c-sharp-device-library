using AVCoders.Core;
using AVCoders.Core.Tests;
using AVCoders.MediaPlayer;
using Moq;

namespace AVCoders.Display.Tests;

public class CecDisplayTest
{
    private readonly CecDisplay _display;
    private readonly Mock<SerialClient> _mockClient = TestFactory.CreateSerialClient();
    private static readonly RemoteButton[] _excludedButtons = 
    [
        RemoteButton.Display, RemoteButton.Eject, 
        RemoteButton.PopupMenu, RemoteButton.TopMenu,
        RemoteButton.PowerOn, RemoteButton.PowerOff,
        RemoteButton.Guide, RemoteButton.Home, RemoteButton.Menu
    ];
    public static IEnumerable<object[]> RemoteButtonValues()
    {
        return Enum.GetValues(typeof(RemoteButton))
            .Cast<RemoteButton>()
            .Where(rb => !_excludedButtons.Contains(rb))
            .Select(rb => new object[] { rb });
    }
    
    public CecDisplayTest()
    {
        _display = new CecDisplay(_mockClient.Object, "Test display");
    }

    [Fact]
    public void PowerOn_SendsTheUserControlPressedAndReleasedCommands()
    {
        _display.PowerOn();
        
        _mockClient.Verify(x => x.Send(new []{ '\x40', '\x44', '\x6D'}));
        _mockClient.Verify(x => x.Send(new []{ '\x40', '\x45'}));
    }

    [Fact]
    public void PowerOff_SendsTheUserControlPressedAndReleasedCommands()
    {
        _display.PowerOff();
        
        _mockClient.Verify(x => x.Send(new []{ '\x40', '\x44', '\x6C'}));
        _mockClient.Verify(x => x.Send(new []{ '\x40', '\x45'}));
    }
    
    [Theory]
    [InlineData(0, '\x00')]
    [InlineData(50, '\x3F')]
    [InlineData(100, '\x7F')]
    public void SetVolume_SendsTheCommand(int percentage, char expectedVolume)
    {
        _display.SetVolume(percentage);
        _mockClient.Verify(x => x.Send(new []{ '\x40', '\x7a', expectedVolume}));
    }

    [Theory]
    [InlineData(MuteState.On, '\x65')]
    [InlineData(MuteState.Off, '\x66')]
    public void SetAudioMute_SendsTheCommand(MuteState input, char expected)
    {
        _display.SetAudioMute(input);
        
        _mockClient.Verify(x => x.Send(new []{ '\x40', '\x44', expected}));
        _mockClient.Verify(x => x.Send(new []{ '\x40', '\x45'}));
    }

    [Fact]
    public void SetVolume_Unmutes()
    {
        _display.SetAudioMute(MuteState.On);
        _display.SetVolume(30);
        
        Assert.Equal(MuteState.Off, _display.AudioMute);
    }

    [Theory]
    [MemberData(nameof(RemoteButtonValues))]
    public void SendIRCode_HandlesAllRemoteButtonValues(RemoteButton button)
    {
        _mockClient.Invocations.Clear();

        _display.SendIRCode(button);

        Assert.Contains(button, _display.SupportedButtons);
        _mockClient.Verify(x => x.Send(It.IsAny<char[]>()), Times.AtLeastOnce);
    }

    [Fact]
    public void SetChannel_SendsTheCommand()
    {
        _display.SetChannel(12);

        _mockClient.Verify(x => x.Send(new []{'\x40', '\x44', '\x21'}));
        _mockClient.Verify(x => x.Send(new []{'\x40', '\x44', '\x22'}));
    }

    [Fact]
    public void HandleResponse_UpdatesTheCommunicationState()
    {
        Assert.Equal(CommunicationState.NotAttempted, _display.CommunicationState);

        _mockClient.Object.ResponseHandlers!.Invoke("\x0F\x90\x00");

        Assert.Equal(CommunicationState.Okay, _display.CommunicationState);
    }

    [Fact]
    public void SupportedButtons_MatchTheRemoteMap()
    {
        Assert.Contains(RemoteButton.Enter, _display.SupportedButtons);
        Assert.Contains(RemoteButton.Red, _display.SupportedButtons);
        Assert.DoesNotContain(RemoteButton.Guide, _display.SupportedButtons);
        Assert.DoesNotContain(RemoteButton.Home, _display.SupportedButtons);
        Assert.DoesNotContain(RemoteButton.Menu, _display.SupportedButtons);
    }

    [Fact]
    public void SendIRCode_UnsupportedButtonSendsNothing()
    {
        _display.SendIRCode(RemoteButton.Home);

        _mockClient.Verify(x => x.Send(It.IsAny<byte[]>()), Times.Never);
        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void SupportedButtons_AreExactlyTheButtonsTheTestExpects()
    {
        Assert.Equal(Enum.GetValues<RemoteButton>().Except(_excludedButtons).OrderBy(b => b), _display.SupportedButtons.OrderBy(b => b));
    }

    public static IEnumerable<object[]> ExcludedButtonValues() => _excludedButtons.Select(rb => new object[] { rb });

    [Theory]
    [MemberData(nameof(ExcludedButtonValues))]
    public void SendIRCode_ExcludedButtonsAreNotSupportedAndSendNothing(RemoteButton button)
    {
        _mockClient.Invocations.Clear();
        _display.SendIRCode(button);

        Assert.DoesNotContain(button, _display.SupportedButtons);
        _mockClient.Verify(x => x.Send(It.IsAny<char[]>()), Times.Never);
    }
}
