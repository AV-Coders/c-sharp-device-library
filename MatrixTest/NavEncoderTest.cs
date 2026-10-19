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

    private class TestSshClient() : SshClient("host", 22023, "Test", CommandStringFormat.Ascii)
    {
        private readonly object _sentLock = new();
        private readonly List<string> _sent = [];
        public List<string> Sent { get { lock (_sentLock) return [.._sent]; } }
        public override void Send(string message) { lock (_sentLock) _sent.Add(message); }
        public override void Send(byte[] bytes) { }
        public override void Connect() { }
        public override void Reconnect() { }
        public override void Disconnect()
        {
            DesiredConnectionState = ConnectionState.Disconnected;
            ConnectionState = ConnectionState.Disconnected;
        }
        protected override Task ProcessSendQueue(CancellationToken token) => Task.CompletedTask;
        protected override Task CheckConnectionState(CancellationToken token) => Task.CompletedTask;
        protected override Task Receive(CancellationToken token) => Task.CompletedTask;
    }

    [Fact]
    public async Task Poll_SendsTheDeviceQueriesThroughTheNavigator()
    {
        var sshClient = new TestSshClient();
        _ = new NavEncoder("Encoder", "1.1.1.1", new Navigator("Nav", sshClient));

        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.Contains($"{{1.1.1.1:{Navigator.EscapeHeader}DNUM\r}}\r", sshClient.Sent);
    }

    [Fact]
    public async Task Poll_WhileTheNavigatorIsDisconnectedOnRequest_SendsNothing()
    {
        var sshClient = new TestSshClient();
        var navigator = new Navigator("Nav", sshClient);
        sshClient.Disconnect();

        _ = new NavEncoder("Encoder", "1.1.1.1", navigator);
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.Empty(sshClient.Sent);
    }

    [Fact]
    public void ResponseHandler_ProcessesGeneralSystemInfo()
    {
        Action<string> theAction = (Action<string>)_navigatorMock.Invocations[0].Arguments[1];
        theAction.Invoke("SigI1*HdcpI2*HdcpO2*ResI1920x1080@60*AudI0*StrmI1*Lnk1*Enc");

        _inputSyncInfoHandlerMock.Verify(x => x.Invoke(ConnectionState.Connected, "1920x1080@60", HdcpStatus.Active));
    }

    [Fact]
    public void ResponseHandler_ProcessesGeneralSystemInfoWithNoSignal()
    {
        Action<string> theAction = (Action<string>)_navigatorMock.Invocations[0].Arguments[1];
        theAction.Invoke("SigI0*HdcpI1*HdcpO0*ResI0x0@0*AudI0*StrmI0*Lnk1*Enc");

        _inputSyncInfoHandlerMock.Verify(x => x.Invoke(ConnectionState.Disconnected, "", HdcpStatus.Inactive));
    }

    [Fact]
    public void ResponseHandler_ProcessesGeneralSystemInfoWithNoInputConnected()
    {
        Action<string> theAction = (Action<string>)_navigatorMock.Invocations[0].Arguments[1];
        theAction.Invoke("SigI0*HdcpI0*HdcpO0*ResINOT DETECTED*AudI1*StrmI1*Lnk1*Enc");

        _inputSyncInfoHandlerMock.Verify(x => x.Invoke(ConnectionState.Disconnected, "", HdcpStatus.Unknown));
    }

    [Theory]
    [InlineData("In00 0", ConnectionState.Disconnected)]
    [InlineData("In00 1", ConnectionState.Connected)]
    public void ResponseHandler_ProcessesInputStatus(string response, ConnectionState expectedState)
    {
        Action<string> theAction = (Action<string>)_navigatorMock.Invocations[0].Arguments[1];
        theAction.Invoke(response);
        
        Assert.Equal(expectedState, _inputSyncInfoHandlerMock.Invocations[0].Arguments[0]);
    }
    
    [Theory]
    [InlineData("HdcpI1", HdcpStatus.Inactive)]
    [InlineData("HdcpI2", HdcpStatus.Active)]
    public void ResponseHandler_ProcessesHDCPStatus(string response, HdcpStatus expectedState)
    {
        Action<string> theAction = (Action<string>)_navigatorMock.Invocations[0].Arguments[1];
        theAction.Invoke(response);
        
        Assert.Equal(expectedState, _inputSyncInfoHandlerMock.Invocations[0].Arguments[2]);
    }
    
    
}