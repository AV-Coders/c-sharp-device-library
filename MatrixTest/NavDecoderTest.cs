using AVCoders.Core;
using Moq;

namespace AVCoders.Matrix.Tests;

public class NavDecoderTest
{
    private readonly Mock<SshClient> _mockSshClient;
    private readonly NavDecoder _navDecoder;
    private readonly Mock<Navigator> _navigatorMock;
    private readonly Mock<SyncInfoHandler> _outputSyncInfoHandlerMock;
    private readonly Mock<AddressChangeHandler> _addressChangeHandlerMock;

    public NavDecoderTest()
    {
        _mockSshClient = new("foo", Navigator.DefaultPort, "Test");
        _navigatorMock = new Mock<Navigator>("NAV!", _mockSshClient.Object);
        _navDecoder = new NavDecoder("Decoder", "1.1.1.1", _navigatorMock.Object);
        _outputSyncInfoHandlerMock = new Mock<SyncInfoHandler>();
        _navDecoder.OutputStatusChangedHandlers += _outputSyncInfoHandlerMock.Object;
        _addressChangeHandlerMock = new Mock<AddressChangeHandler>();
        _navDecoder.StreamChangeHandlers += _addressChangeHandlerMock.Object;
    }

    [Fact]
    public void ResponseHandler_ProcessesGeneralSystemInfo()
    {
        Action<string> theAction = (Action<string>)_navigatorMock.Invocations[0].Arguments[1];
        theAction.Invoke("VidI1*HdcpI2*HdcpO2*ResI1920x1080@60*AudI1*StrmI1*Lnk1*Dec");
        _outputSyncInfoHandlerMock.Verify(x => x.Invoke(ConnectionStatus.Connected, "1920x1080@60", HdcpStatus.Unknown));
    }

    [Fact]
    public void ResponseHandler_ProcessesNewStreamId()
    {
        Action<string> theAction = (Action<string>)_navigatorMock.Invocations[0].Arguments[1];
        theAction.Invoke("In3696 All");
        _addressChangeHandlerMock.Verify(x => x.Invoke("3696"));
    }

    [Fact]
    public void ResponseHandler_ProcessesVideoMuteOn()
    {
        Mock<MuteStateHandler> muteStateHandlerMock = new();
        _navDecoder.VideoMuteStateHandlers = muteStateHandlerMock.Object;
        Action<string> theAction = (Action<string>)_navigatorMock.Invocations[0].Arguments[1];
        theAction.Invoke("Vmt2");
        muteStateHandlerMock.Verify(x => x.Invoke(MuteState.On));
    }

    [Fact]
    public void ResponseHandler_ProcessesVideoMuteOff()
    {
        Mock<MuteStateHandler> muteStateHandlerMock = new();
        _navDecoder.VideoMuteStateHandlers = muteStateHandlerMock.Object;
        Action<string> theAction = (Action<string>)_navigatorMock.Invocations[0].Arguments[1];
        theAction.Invoke("Vmt0");
        muteStateHandlerMock.Verify(x => x.Invoke(MuteState.Off));
    }

    [Fact]
    public void ResponseHandler_ProcessesAudioMuteOn()
    {
        Mock<MuteStateHandler> muteStateHandlerMock = new();
        _navDecoder.AudioMuteStateHandlers = muteStateHandlerMock.Object;
        Action<string> theAction = (Action<string>)_navigatorMock.Invocations[0].Arguments[1];
        theAction.Invoke("Amt1*1");
        muteStateHandlerMock.Verify(x => x.Invoke(MuteState.On));
    }

    [Fact]
    public void ResponseHandler_ProcessesAudioMuteOff()
    {
        Mock<MuteStateHandler> muteStateHandlerMock = new();
        _navDecoder.AudioMuteStateHandlers = muteStateHandlerMock.Object;
        Action<string> theAction = (Action<string>)_navigatorMock.Invocations[0].Arguments[1];
        theAction.Invoke("Amt1*0");
        muteStateHandlerMock.Verify(x => x.Invoke(MuteState.Off));
    }

    [Fact]
    public void SetInput_SendsTheCommand()
    {
        Action<string> theAction = (Action<string>)_navigatorMock.Invocations[0].Arguments[1];
        theAction.Invoke("Dnum101");
        _navDecoder.SetInput(1);
        _navigatorMock.Verify(x => x.RouteAV(1, 101));
    }

    [Fact]
    public void SetInput_SendsTheDerouteCommand()
    {
        Action<string> theAction = (Action<string>)_navigatorMock.Invocations[0].Arguments[1];
        theAction.Invoke("Dnum101");
        _navDecoder.SetInput(0);
        _navigatorMock.Verify(x => x.RouteAV(0, 101));
    }
}