using AVCoders.Core;
using Moq;

namespace AVCoders.Matrix.Tests;

public class NavEncoderTest
{
    private readonly NavEncoder _navEncoder;
    private readonly Mock<Navigator> _navigatorMock;
    private readonly Mock<SyncInfoHandler> _inputSyncInfoHandlerMock;

    public NavEncoderTest()
    {
        Mock<SshClient> mockSshClient = new("foo", Navigator.DefaultPort, "Test");
        _navigatorMock = new Mock<Navigator>("NAV!", mockSshClient.Object);
        _navEncoder = new NavEncoder("Encoder", "1.1.1.1", _navigatorMock.Object);
        _inputSyncInfoHandlerMock = new Mock<SyncInfoHandler>();
        _navEncoder.InputStatusChangedHandlers += _inputSyncInfoHandlerMock.Object;

    }

    [Fact]
    public void ResponseHandler_ProcessesGeneralSystemInfo()
    {
        Action<string> theAction = (Action<string>)_navigatorMock.Invocations[0].Arguments[1];
        theAction.Invoke("SigI1*HdcpI2*HdcpO2*ResI1920x1080@60*AudI0*StrmI1*Lnk1*Enc");
        
        _inputSyncInfoHandlerMock.Verify(x => x.Invoke(ConnectionStatus.Connected, "1920x1080@60", HdcpStatus.Unknown));
    }
    
}