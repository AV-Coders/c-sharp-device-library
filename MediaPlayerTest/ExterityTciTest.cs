using AVCoders.Core;

namespace AVCoders.MediaPlayer.Tests;

public class ExterityTciTest
{
    private readonly ExterityTci _interface;
    private readonly Mock<CommunicationClient> _mockClient;

    public ExterityTciTest()
    {
        _mockClient = new Mock<CommunicationClient>();
        _interface = new ExterityTci(_mockClient.Object);
    }
    
    [Fact]
    public void PowerOn_SetsTheModeToAV()
    {
        string expectedCommand = "^set:currentMode:av!\n";
        _interface.PowerOn();
            
        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void PowerOff_SetsTheModeToOff()
    {
        string expectedCommand = "^set:currentMode:off!\n";
        _interface.PowerOff();
            
        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    //^send:rm_<key>!
    [Fact]
    public void ChannelUp_SendsTheCommand()
    {
        string expectedCommand = "^send:rm_chup!\n";
        _interface.ChannelUp();
            
        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }
        
    [Fact]
    public void ChannelDown_SendsTheCommand()
    {
        string expectedCommand = "^send:rm_chdown!\n";
        _interface.ChannelDown();
            
        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }
        
    [Fact]
    public void VolumeUp_SendsTheCommand()
    {
        string expectedCommand = "^send:rm_volup!\n";
        _interface.VolumeUp();
            
        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }
        
    [Fact]
    public void VolumeDown_SendsTheCommand()
    {
        string expectedCommand = "^send:rm_voldown!\n";
        _interface.VolumeDown();
            
        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Theory]
    [InlineData(MuteState.On, "^set:mute:on!\n")]
    [InlineData(MuteState.Off, "^set:mute:off!\n")]
    public void SetAudioMute_SendsTheCommand(MuteState state, string expectedCommand)
    {
        _interface.SetAudioMute(state);
        
        _mockClient.Verify(x=> x.Send(expectedCommand));
    }

    [Fact]
    public void SetChannel_SendsTheCommands()
    {
        _interface.SetChannel(123);
        
        _mockClient.Verify(x => x.Send("^send:rm_1!\n"));
        _mockClient.Verify(x => x.Send("^send:rm_2!\n"));
        _mockClient.Verify(x => x.Send("^send:rm_3!\n"));
        _mockClient.Verify(x => x.Send("^send:rm_enter!\n"));
    }
}