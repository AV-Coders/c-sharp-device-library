using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Matrix.Tests;

public class NavigatorTest
{
    private readonly Navigator _navigator;
    private readonly Mock<SshClient> _mockSshClient = TestFactory.CreateSshClient();
    public const string EscapeHeader = "\x1b";

    public NavigatorTest()
    {
        _navigator = new Navigator("Foo", _mockSshClient.Object);
    }

    [Fact]
    public void ResponseHandler_ForwardsToTheDevice()
    {
        Mock<Action<string>> mockResponseHandler = new Mock<Action<string>>();
        _navigator.RegisterDevice("10.1.3.207", mockResponseHandler.Object);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}VidI0*HdcpI0*HdcpO0*ResI0x0@0*AudI0*StrmI0*Lnk1*Dec");
        
        mockResponseHandler.Verify(x => x.Invoke("VidI0*HdcpI0*HdcpO0*ResI0x0@0*AudI0*StrmI0*Lnk1*Dec"));
    }

    [Fact]
    public void ResponseHandler_ForwardsTieResponsesToTheOutput()
    {
        Mock<Action<string>> mockResponseHandler = new Mock<Action<string>>();
        _navigator.RegisterDevice("0671o", mockResponseHandler.Object);
        _mockSshClient.Object.ResponseHandlers!.Invoke("Out671 In663 All");

        mockResponseHandler.Verify(x => x.Invoke("In663 All"));
    }

    [Fact]
    public void ResponseHandler_ForwardsUntieResponsesToTheOutput()
    {
        Mock<Action<string>> mockResponseHandler = new Mock<Action<string>>();
        _navigator.RegisterDevice("0661o", mockResponseHandler.Object);
        _mockSshClient.Object.ResponseHandlers!.Invoke("Out661 In00 All");

        mockResponseHandler.Verify(x => x.Invoke("In00 All"));
    }

    [Fact]
    public void ResponseHandler_IgnoresUnregisteredTieResponses()
    {
        _mockSshClient.Object.ResponseHandlers!.Invoke("Out999 In1 All");
        _mockSshClient.Object.ResponseHandlers!.Invoke("In0i Usb");
        _mockSshClient.Object.ResponseHandlers!.Invoke("E10");
    }

    [Fact]
    public void ResponseHandler_AppliesDevicePresenceUpdates()
    {
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum671");

        _mockSshClient.Object.ResponseHandlers!.Invoke("DevpP*671o*0");
        Assert.Equal(ConnectionState.Disconnected, decoder.DeviceConnectionState);

        _mockSshClient.Object.ResponseHandlers!.Invoke("DevpP*671o*1");
        Assert.Equal(ConnectionState.Connected, decoder.DeviceConnectionState);
    }

    [Fact]
    public void ConnectionState_SeedsSystemState()
    {
        _mockSshClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);

        _mockSshClient.Verify(x => x.Send($"{EscapeHeader}3CV\r"));
        _mockSshClient.Verify(x => x.Send($"{EscapeHeader}Inventory*I*RPRT\r"));
        _mockSshClient.Verify(x => x.Send($"{EscapeHeader}Inventory*O*RPRT\r"));
        _mockSshClient.Verify(x => x.Send($"{EscapeHeader}Ties*A*RPRT\r"));
    }

    [Fact]
    public void ResponseHandler_AppliesInventoryReports()
    {
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum3");

        _mockSshClient.Object.ResponseHandlers!.Invoke("Rprt*Inventory*O*112");
        Assert.Equal(ConnectionState.Disconnected, decoder.DeviceConnectionState);

        _mockSshClient.Object.ResponseHandlers!.Invoke("Rprt*Inventory*O*111");
        Assert.Equal(ConnectionState.Connected, decoder.DeviceConnectionState);
    }

    [Fact]
    public void ResponseHandler_ForwardsTieReportRowsToTheOutput()
    {
        Mock<Action<string>> mockResponseHandler = new Mock<Action<string>>();
        _navigator.RegisterDevice("0671o", mockResponseHandler.Object);

        _mockSshClient.Object.ResponseHandlers!.Invoke("671\t663\t661");
        mockResponseHandler.Verify(x => x.Invoke("In663 Vid"));
        mockResponseHandler.Verify(x => x.Invoke("In661 Aud"));

        _mockSshClient.Object.ResponseHandlers!.Invoke("671\t---\t----");
        mockResponseHandler.Verify(x => x.Invoke("In0 Vid"));
        mockResponseHandler.Verify(x => x.Invoke("In0 Aud"));
    }

    [Fact]
    public void DeviceNumberDiscovery_QueriesEndpointState()
    {
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum671");

        _mockSshClient.Verify(x => x.Send($"{EscapeHeader}P*671oDEVP\r"));
        _mockSshClient.Verify(x => x.Send($"{EscapeHeader}671%\r"));
        _mockSshClient.Verify(x => x.Send($"{EscapeHeader}671$\r"));
    }

    [Fact]
    public void RouteAv_SendsTheCommand()
    {
        _navigator.RouteAV(1, 101);
        _mockSshClient.Verify(x => x.Send($"{EscapeHeader}1*101!\r"));
    }

    [Fact]
    public void RouteAv_TracksTheDesiredRouteOnTheDecoder()
    {
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum671");

        _navigator.RouteAV(5, 671);
        Assert.Equal(5, decoder.DesiredVideoInput);
        Assert.Equal(5, decoder.DesiredAudioInput);
    }

    [Fact]
    public void RouteVideo_TracksTheDesiredRouteOnTheDecoder()
    {
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum671");

        _navigator.RouteVideo(5, 671);
        Assert.Equal(5, decoder.DesiredVideoInput);
        Assert.Null(decoder.DesiredAudioInput);
    }

    [Fact]
    public void RouteAudio_TracksTheDesiredRouteOnTheDecoder()
    {
        var decoder = new NavDecoder("Decoder", "10.1.3.207", _navigator);
        _mockSshClient.Object.ResponseHandlers!.Invoke("{10.1.3.207}Dnum671");

        _navigator.RouteAudio(5, 671);
        Assert.Null(decoder.DesiredVideoInput);
        Assert.Equal(5, decoder.DesiredAudioInput);
    }

    [Fact]
    public void RouteAudio_SendsTheCommand()
    {
        _navigator.RouteAudio(111, 101);
        _mockSshClient.Verify(x => x.Send($"{EscapeHeader}111*101$\r"));
    }

    [Fact]
    public void RouteVideo_SendsTheCommand()
    {
        _navigator.RouteVideo(123, 121);
        _mockSshClient.Verify(x => x.Send($"{EscapeHeader}123*121%\r"));
    }
}