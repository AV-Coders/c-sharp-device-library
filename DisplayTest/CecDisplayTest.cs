using AVCoders.Core;
using Moq;

namespace AVCoders.Display.Tests;

public class CecDisplayTest
{
    private readonly CecDisplay _display;
    private readonly Mock<SerialClient> _mockClient;
    
    public CecDisplayTest()
    {
        _mockClient = new Mock<SerialClient>();
        _display = new CecDisplay(_mockClient.Object);
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
        
        Assert.Equal(MuteState.Off, _display.GetAudioMute());
    }
}