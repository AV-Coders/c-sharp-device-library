using AVCoders.Core;
using Moq;
using WirelessPresenter;

namespace WirelessPresenterTest;

public class ExtronSharelinkProTest
{
    private readonly ExtronSharelinkPro _sharelink;
    private readonly Mock<CommunicationClient> _mockClient = new ("foo");
    private const string EscapeHeader = "\x1b";
    private readonly ExtronSharelinkUser _user = new ExtronSharelinkUser("421380106", String.Empty, String.Empty,
        String.Empty, false, String.Empty, String.Empty);

    public ExtronSharelinkProTest()
    {
        _sharelink = new ExtronSharelinkPro(_mockClient.Object, "Test Sharelink");
    }
    
    [Theory]
    [InlineData("SharQ1", PowerState.Off)] // Standby
    [InlineData("SharQ2", PowerState.On)] // Connected
    [InlineData("SharQ3", PowerState.On)] // Expo
    [InlineData("SharQ4", PowerState.On)] // Expo Standby
    [InlineData("SharQ5", PowerState.On)] // Sharing
    public void HandleResponse_UpdatesPowerState(string response, PowerState expectedState)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        Assert.Equal(expectedState, _sharelink.PowerState);
    }

    [Fact]
    public void HandleResponse_RequestsConnectedUserInfo()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("SharK1\n");
        _mockClient.Verify(x => x.Send($"{EscapeHeader}L0SHAR\r"));
    }

    [Fact]
    public void HandleResponse_UpdatesTheConnectedUsersList()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("SharL421380106*Ac-16*1963993098*22F76,airplay,screen*1*0*0*10000*10000*0*622338880*Ac-16\n");
        Assert.Single(_sharelink.ConnectedUsers);
        var user = _sharelink.ConnectedUsers.First();
        Assert.Equal("421380106", user.ConnectionId);
        Assert.Equal("Ac-16", user.UserName);
        Assert.Equal("1963993098", user.StreamId);
        Assert.Equal("22F76,airplay,screen", user.Platform);
        Assert.True(user.ConnectionApproved);
        Assert.Equal("622338880", user.VideoPortId);
        Assert.Equal("Ac-16", user.DeviceName);
    }

    [Fact]
    public void HandleResponse_HandlesMoreThanOneUserResponse()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("SharL827494424*Ac-16*1663926294*22F76,airplay,casting*1*0*0*5000*10000*69*621123328*Ac-16\n");
        _mockClient.Object.ResponseHandlers!.Invoke("84234268*AC-NUCExtreme*1068064792*windows,extron,none*1*5000*0*5000*10000*46*623238272*AC-NUCExtreme\n");
        
        Assert.Equal(2, _sharelink.ConnectedUsers.Count);
        var user = _sharelink.ConnectedUsers[1];
        Assert.Equal("84234268", user.ConnectionId);
        Assert.Equal("AC-NUCExtreme", user.UserName);
        Assert.Equal("1068064792", user.StreamId);
        Assert.Equal("windows,extron,none", user.Platform);
        Assert.True(user.ConnectionApproved);
        Assert.Equal("623238272", user.VideoPortId);
        Assert.Equal("AC-NUCExtreme", user.DeviceName);
    }

    [Fact]
    public void HandleResponse_TriggersAConnectedUsersEvent()
    {
        Mock<IntHandler> mockHandler = new();
        _sharelink.ConnectedUsersHandlers += mockHandler.Object;
        _mockClient.Object.ResponseHandlers!.Invoke("SharL827494424*Ac-16*1663926294*22F76,airplay,casting*1*0*0*5000*10000*69*621123328*Ac-16\n");
        _mockClient.Object.ResponseHandlers!.Invoke("84234268*AC-NUCExtreme*1068064792*windows,extron,none*1*5000*0*5000*10000*46*623238272*AC-NUCExtreme\n");
        
        Thread.Sleep(TimeSpan.FromSeconds(3));
        mockHandler.Verify(x => x.Invoke(2));
    }

    [Fact]
    public void DisconnectUser_SendsTheCommand()
    {
        _sharelink.DisconnectUser(_user);
        _mockClient.Verify(x => x.Send($"{EscapeHeader}D421380106SHAR\r"));
    }

    [Fact]
    public void ApproveConnectionRequest_SendsTheCommand()
    {
        _sharelink.ApproveConnectionRequest(_user);
        _mockClient.Verify(x => x.Send($"{EscapeHeader}C421380106*1SHAR\r"));
    }

    [Fact]
    public void DenyConnectionRequest_SendsTheCommand()
    {
        _sharelink.DenyConnectionRequest(_user);
        _mockClient.Verify(x => x.Send($"{EscapeHeader}C421380106*0SHAR\r"));
    }

    [Fact]
    public void ApproveShareRequest_SendsTheCommand()
    {
        _sharelink.ApproveShareRequest(_user);
        _mockClient.Verify(x => x.Send($"{EscapeHeader}R421380106*1SHAR\r"));
    }

    [Fact]
    public void DenyShareRequest_SendsTheCommand()
    {
        _sharelink.DenyShareRequest(_user);
        _mockClient.Verify(x => x.Send($"{EscapeHeader}R421380106*0SHAR\r"));
    }

    [Theory]
    [InlineData("UserChg")]
    public void HandleResponse_RequestsUserStatus(string response)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        _mockClient.Verify(x => x.Send($"{EscapeHeader}KSHAR\r"));
    }
}