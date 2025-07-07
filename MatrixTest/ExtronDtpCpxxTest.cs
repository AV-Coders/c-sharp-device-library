using System.Reflection;
using AVCoders.Core;
using Moq;

namespace AVCoders.Matrix.Tests;

public class ExtronDtpCpxxTest
{
    private readonly ExtronDtpCpxx _switcher;
    private readonly Mock<CommunicationClient> _mockClient = new Mock<CommunicationClient>("foo");
    private readonly string EscapeHeader = "\x1b";

    public ExtronDtpCpxxTest()
    {
        _switcher = new ExtronDtpCpxx(_mockClient.Object, 8, "test matrix");
        _mockClient.Object.ResponseHandlers!.Invoke("Inf00*DTPCP108");
    }

    [Fact]
    public void SendCommand_DoesNotManipulateInput()
    {
        String input = "Foo";

        var method = _switcher.GetType().GetMethod("SendCommand", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(_switcher, new[] { input });
        
        _mockClient.Verify(x => x.Send(input), Times.Once);
    }

    [Fact]
    public void SendCommand_ReportsCommunicationIsOkay()
    {
        String input = "Foo";
        
        var method = _switcher.GetType().GetMethod("SendCommand", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(_switcher, new[] { input });

        Assert.Equal(CommunicationState.Okay, _switcher.CommunicationState);
    }

    [Fact]
    public void SendCommand_ReportsCommunicationHasFailed()
    {
        String input = "Foo";

        _mockClient.Setup(client => client.Send(It.IsAny<String>())).Throws(new IOException("Oh No!"));
        
        var method = _switcher.GetType().GetMethod("SendCommand", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(_switcher, new[] { input });

        Assert.Equal(CommunicationState.Error, _switcher.CommunicationState);
    }

    [Fact]
    public void RouteVideo_SendsTheCommand()
    {
        String expectedRouteCommand = "1*3%";
        _switcher.RouteVideo(1, 3);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void RouteVideo_RoutesToAllWithOutput0()
    {
        String expectedRouteCommand = "3*%";
        _switcher.RouteVideo(3, 0);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void RouteAudio_SendsTheCommand()
    {
        String expectedRouteCommand = "1*3$";
        _switcher.RouteAudio(1, 3);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void RouteAudio_RoutesToAllWithOutput0()
    {
        String expectedRouteCommand = "1*$";
        _switcher.RouteAudio(1, 0);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void RouteAV_SendsTheCommand()
    {
        String expectedRouteCommand = "1*3!";
        _switcher.RouteAV(1, 3);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void RouteAV_RoutesToAllWithOutput0()
    {
        String expectedRouteCommand = "1*!";
        _switcher.RouteAV(1, 0);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void SetSyncTimeout_SendsTheCommand()
    {
        String expectedCommand = "\u001bT0*3SSAV\u0027";
        _switcher.SetSyncTimeout(0, 3);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void SetSyncTimeout_IgnoresInvalidTimeouts()
    {
        _switcher.SetSyncTimeout(502, 1);

        _mockClient.Verify(x => x.Send(It.Is<string>(s => s.Contains("SSAV"))), Times.Never);
    }

    [Fact]
    public void SetSyncTimeout_SetsToNeverDropSync()
    {
        String expectedCommand = "\u001bT501*1SSAV\u0027";
        _switcher.SetSyncTimeout(501, 1);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Theory]
    [InlineData("Frq00 0000000000\r", 10)]
    [InlineData("Frq00 00000000\r", 8)]
    public void HandleResponse_SetsNumberOfInputs(string response, int expectedNumberOfInputs)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        Assert.Equal(expectedNumberOfInputs, _switcher.Inputs.Count);
    }

    [Theory]
    [InlineData("Ityp01*3\r", ConnectionState.Connected)]
    [InlineData("Ityp01*4\r", ConnectionState.Connected)]
    [InlineData("Ityp01*0\r", ConnectionState.Disconnected)]
    public void HandleResponse_UpdatesVideoSyncState(string response, ConnectionState expectedConnectionStatus)
    {
        Mock<SyncInfoHandler> mockSyncInfoHandler = new Mock<SyncInfoHandler>();
        _mockClient.Object.ResponseHandlers!.Invoke("Frq00 0000000000\r");
        _switcher.Inputs[0].InputStatusChangedHandlers += mockSyncInfoHandler.Object;
        _mockClient.Object.ResponseHandlers!.Invoke(response);

        mockSyncInfoHandler.Verify(x => x.Invoke(expectedConnectionStatus, String.Empty, HdcpStatus.Unknown));
    }
    
    [Theory]
    [InlineData("Nmi1,Laptop\r", 0, "Laptop")]
    [InlineData("Nmi2,Wireless\r", 1, "Wireless")]
    [InlineData("Nmi3,BluRay\r", 2, "BluRay")]
    public void HandleResponse_UpdatesInputName(string response, int index, string expectedName)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        Assert.Equal(expectedName, _switcher.Inputs[index].Name);
    }
    
    [Theory]
    [InlineData("Nmo1,Projector\r", 0, "Projector")]
    [InlineData("Nmo2,LCD\r", 1, "LCD")]
    [InlineData("Nmo3,Fountain\r", 2, "Fountain")]
    public void HandleResponse_UpdatesOutputName(string response, int index, string expectedName)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        Assert.Equal(expectedName, _switcher.ComposedOutputs[index].Primary.Name);
        Assert.Equal($"{expectedName} - B", _switcher.ComposedOutputs[index].Secondary.Name);
    }

    [Theory]
    [InlineData("Inf00*DTPCP82", 8, 2)]
    [InlineData("Inf00*DTPCP84", 8, 4)]
    [InlineData("Inf00*DTPCP86", 8, 6)]
    [InlineData("Inf00*DTPCP108", 10, 8)]
    public void HandleResponse_SetsInputsAndOutputsFromModel(string response, int inputs, int outputs)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        Assert.Equal(inputs, _switcher.Inputs.Count);
        Assert.Equal(outputs, _switcher.ComposedOutputs.Count);
    }

    [Theory]
    [InlineData("Hplg01", "1")]
    [InlineData("Hplg02", "2")]
    [InlineData("Hplg03", "3")]
    [InlineData("Hplg05A", "5A")]
    [InlineData("Hplg05B", "5B")]
    public void HandleResponse_RequestsOutputFormatOnHotplugEvent(string eventResponse, string outputNumber)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(eventResponse);
        _mockClient.Verify(x => x.Send($"{EscapeHeader}O{outputNumber}HDCP\r"), Times.AtLeastOnce);
    }

    [Theory]
    [InlineData("HdcpI01*1", 0, ConnectionState.Connected, HdcpStatus.Active)]
    [InlineData("HdcpI01*0", 0, ConnectionState.Disconnected, HdcpStatus.Unknown)]
    [InlineData("HdcpI02*2", 1, ConnectionState.Connected, HdcpStatus.NotSupported)]
    [InlineData("HdcpI02*0", 1, ConnectionState.Disconnected, HdcpStatus.Unknown)]
    [InlineData("HdcpI03*2", 2, ConnectionState.Connected, HdcpStatus.NotSupported)]
    [InlineData("HdcpI03*0", 2, ConnectionState.Disconnected, HdcpStatus.Unknown)]
    [InlineData("HdcpI04*1", 3, ConnectionState.Connected, HdcpStatus.Active)]
    [InlineData("HdcpI04*0", 3, ConnectionState.Disconnected, HdcpStatus.Unknown)]
    public void HandleResponse_SetsInputConnectionStatusForSingleOutputNumbers(string eventResponse, int arrayIndex, ConnectionState expected, HdcpStatus expectedHdcpStatus)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(eventResponse);
        Assert.Equal(expected, _switcher.Inputs[arrayIndex].InputConnectionStatus);
        Assert.True(_switcher.Inputs[arrayIndex].InUse);
    }

    [Theory]
    [InlineData("HdcpO1*1", 0, ConnectionState.Connected, HdcpStatus.NotSupported)]
    [InlineData("HdcpO1*0", 0, ConnectionState.Disconnected, HdcpStatus.Unknown)]
    [InlineData("HdcpO2*2", 1, ConnectionState.Connected, HdcpStatus.Available)]
    [InlineData("HdcpO2*0", 1, ConnectionState.Disconnected, HdcpStatus.Unknown)]
    [InlineData("HdcpO3*3", 2, ConnectionState.Connected, HdcpStatus.Active)]
    [InlineData("HdcpO3*0", 2, ConnectionState.Disconnected, HdcpStatus.Unknown)]
    [InlineData("HdcpO4*1", 3, ConnectionState.Connected, HdcpStatus.NotSupported)]
    [InlineData("HdcpO4*0", 3, ConnectionState.Disconnected, HdcpStatus.Unknown)]
    public void HandleResponse_SetsOutputConnectionStatusForSingleOutputNumbers(string eventResponse, int arrayIndex, ConnectionState expected, HdcpStatus expectedHdcpStatus)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(eventResponse);
        Assert.Equal(expected, _switcher.ComposedOutputs[arrayIndex].Primary.OutputConnectionStatus);
        Assert.Equal(expectedHdcpStatus, _switcher.ComposedOutputs[arrayIndex].Primary.OutputHdcpStatus);
        Assert.True(_switcher.ComposedOutputs[arrayIndex].Primary.InUse);
    }

    [Theory]
    [InlineData("HdcpO5A*1", 4, ConnectionState.Connected, HdcpStatus.NotSupported)]
    [InlineData("HdcpO5A*0", 4, ConnectionState.Disconnected, HdcpStatus.Unknown)]
    [InlineData("HdcpO6A*2", 5, ConnectionState.Connected, HdcpStatus.Available)]
    [InlineData("HdcpO6A*0", 5, ConnectionState.Disconnected, HdcpStatus.Unknown)]
    public void HandleResponse_SetsOutputConnectionStatusForSplitOutputs_Primary(string eventResponse, int arrayIndex, ConnectionState expected, HdcpStatus expectedHdcpStatus)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(eventResponse);
        Assert.Equal(expected, _switcher.ComposedOutputs[arrayIndex].Primary.OutputConnectionStatus);
        Assert.Equal(expectedHdcpStatus, _switcher.ComposedOutputs[arrayIndex].Primary.OutputHdcpStatus);
        Assert.True(_switcher.ComposedOutputs[arrayIndex].Primary.InUse);
    }

    [Theory]
    [InlineData("HdcpO5B*1", 4, ConnectionState.Connected, HdcpStatus.NotSupported)]
    [InlineData("HdcpO5B*0", 4, ConnectionState.Disconnected, HdcpStatus.Unknown)]
    [InlineData("HdcpO6B*2", 5, ConnectionState.Connected, HdcpStatus.Available)]
    [InlineData("HdcpO6B*0", 5, ConnectionState.Disconnected, HdcpStatus.Unknown)]
    public void HandleResponse_SetsOutputConnectionStatusForSplitOutputs_Secondary(string eventResponse, int arrayIndex, ConnectionState expected, HdcpStatus expectedHdcpStatus)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(eventResponse);
        Assert.Equal(expected, _switcher.ComposedOutputs[arrayIndex].Secondary.OutputConnectionStatus);
        Assert.Equal(expectedHdcpStatus, _switcher.ComposedOutputs[arrayIndex].Secondary.OutputHdcpStatus);
        Assert.True(_switcher.ComposedOutputs[arrayIndex].Secondary.InUse);
    }
}