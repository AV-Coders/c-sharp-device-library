using AVCoders.Core;
using Moq;

namespace AVCoders.Matrix.Tests;

public class NavigatorTest
{
    private readonly Navigator _navigator;
    private readonly Mock<SshClient> _mockSshClient;

    public NavigatorTest()
    {
        _mockSshClient = new("foo", Navigator.DefaultPort, "Test");
        _navigator = new Navigator(_mockSshClient.Object);
    }

    [Fact]
    public void ResponseHandler_ForwardsToTheDevice()
    {
        Mock<Action<string>> mockResponseHandler = new Mock<Action<string>>();
        _navigator.RegisterDevice("10.1.3.207", mockResponseHandler.Object);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}VidI0*HdcpI0*HdcpO0*ResI0x0@0*AudI0*StrmI0*Lnk1*Dec");
        
        mockResponseHandler.Verify(x => x.Invoke("VidI0*HdcpI0*HdcpO0*ResI0x0@0*AudI0*StrmI0*Lnk1*Dec"));
    }
}