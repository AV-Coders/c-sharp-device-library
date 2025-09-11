using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Matrix.Tests;

public class NavEncoderTest
{
    private readonly Mock<SshClient> _mockSshClient = TestFactory.CreateSshClient();
    private readonly NavEncoder _navEncoder;
    private readonly Mock<Navigator> _navigatorMock;
    private readonly Mock<SyncInfoHandler> _inputSyncInfoHandlerMock;

    public NavEncoderTest()
    {
        _navigatorMock = new Mock<Navigator>("NAV!", _mockSshClient.Object);
        _navEncoder = new NavEncoder("Encoder", "1.1.1.1", _navigatorMock.Object);
        _inputSyncInfoHandlerMock = new Mock<SyncInfoHandler>();
        _navEncoder.InputStatusChangedHandlers += _inputSyncInfoHandlerMock.Object;

    }

    [Fact]
    public void ResponseHandler_ProcessesGeneralSystemInfo()
    {
        Action<string> theAction = (Action<string>)_navigatorMock.Invocations[0].Arguments[1];
        theAction.Invoke("SigI1*HdcpI2*HdcpO2*ResI1920x1080@60*AudI0*StrmI1*Lnk1*Enc");
        
        _inputSyncInfoHandlerMock.Verify(x => x.Invoke(ConnectionState.Connected, "1920x1080@60", HdcpStatus.Unknown));
    }
    
}