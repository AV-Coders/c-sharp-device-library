using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Matrix.Tests;

// Response strings are taken verbatim from a DTP2 CrossPoint 82 (firmware 1.04) capture.
public class ExtronDtp2Cp82Test
{
    private readonly ExtronDtp2Cp82 _switcher;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();
    private const string EscapeHeader = "\x1b";
    private const string ModelResponse = "Inf00*DTP2 CrossPoint 82";

    public ExtronDtp2Cp82Test()
    {
        _switcher = new ExtronDtp2Cp82(_mockClient.Object, "test matrix");
    }

    private void Respond(string response) => _mockClient.Object.ResponseHandlers!.Invoke(response);

    [Fact]
    public void Constructor_BuildsTheFixedTopology()
    {
        Assert.Equal(8, _switcher.Inputs.Count);
        Assert.Equal(3, _switcher.Outputs.Count);
        Assert.Equal(8, _switcher.NumberOfInputs);
        Assert.Equal(2, _switcher.NumberOfOutputs);
        Assert.Equal(8, _switcher.GetInputs().Count);
        Assert.Equal(3, _switcher.GetOutputs().Count);
        Assert.Equal("Loop Out", _switcher.Outputs[2].Name);
        Assert.Equal(3, _switcher.Outputs[2].Number);
    }

    [Fact]
    public void SendCommand_DoesNotManipulateInput()
    {
        string input = "Foo";

        var method = _switcher.GetType().GetMethod("SendCommand", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(_switcher, [input]);

        _mockClient.Verify(x => x.Send(input), Times.Once);
    }

    [Fact]
    public void SendCommand_ReportsCommunicationIsOkay()
    {
        var method = _switcher.GetType().GetMethod("SendCommand", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(_switcher, ["Foo"]);

        Assert.Equal(CommunicationState.Okay, _switcher.CommunicationState);
    }

    [Fact]
    public void SendCommand_ReportsCommunicationHasFailed()
    {
        _mockClient.Setup(client => client.Send(It.IsAny<string>())).Throws(new IOException("Oh No!"));

        var method = _switcher.GetType().GetMethod("SendCommand", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(_switcher, ["Foo"]);

        Assert.Equal(CommunicationState.Error, _switcher.CommunicationState);
    }

    [Theory]
    [InlineData(ModelResponse)]
    [InlineData("Inf00*DTP2 CrossPoint 82 IPCP SA")]
    public void HandleResponse_QueriesStatusWhenTheModelMatches(string response)
    {
        Respond(response);

        _mockClient.Verify(x => x.Send($"{EscapeHeader}I1VNAM\r"), Times.Once);
        _mockClient.Verify(x => x.Send($"{EscapeHeader}I8VNAM\r"), Times.Once);
        _mockClient.Verify(x => x.Send($"{EscapeHeader}I1HDCP\r"), Times.Once);
        _mockClient.Verify(x => x.Send($"{EscapeHeader}I8HDCP\r"), Times.Once);
        _mockClient.Verify(x => x.Send($"{EscapeHeader}O1VNAM\r"), Times.Once);
        _mockClient.Verify(x => x.Send($"{EscapeHeader}O3VNAM\r"), Times.Once);
        _mockClient.Verify(x => x.Send($"{EscapeHeader}O1HDCP\r"), Times.Once);
        _mockClient.Verify(x => x.Send($"{EscapeHeader}O3HDCP\r"), Times.Once);
        _mockClient.Verify(x => x.Send("1%"), Times.Once);
        _mockClient.Verify(x => x.Send("1$"), Times.Once);
        _mockClient.Verify(x => x.Send("2%"), Times.Once);
        _mockClient.Verify(x => x.Send("2$"), Times.Once);
        _mockClient.Verify(x => x.Send($"{EscapeHeader}LOUT\r"), Times.Once);
        _mockClient.Verify(x => x.Send($"{EscapeHeader}0LS\r"), Times.Once);
        _mockClient.Verify(x => x.Send(It.Is<string>(s => s.Contains("NI") || s.Contains("NO") || s.Contains("AHDCP") || s.Contains("BHDCP"))), Times.Never);
        Assert.Empty(_switcher.GetOngoingIssues());
    }

    [Fact]
    public void HandleResponse_RaisesAnIssueForAnUnexpectedModel()
    {
        Respond("Inf00*DTPCP84 4K");

        var issue = Assert.Single(_switcher.GetOngoingIssues());
        Assert.Equal("model", issue.Key);
        Assert.Contains("DTPCP84 4K", issue.Message);
        _mockClient.Verify(x => x.Send(It.Is<string>(s => s.Contains("VNAM"))), Times.Never);
    }

    [Fact]
    public void HandleResponse_ResolvesTheModelIssueWhenTheRightModelReports()
    {
        Respond("Inf00*DTPCP84 4K");
        Respond(ModelResponse);

        Assert.Empty(_switcher.GetOngoingIssues());
    }

    [Theory]
    [InlineData("VnamI1*--", 0, "--")]
    [InlineData("VnamI2*Codec", 1, "Codec")]
    [InlineData("VnamI7*Doc Cam", 6, "Doc Cam")]
    [InlineData("VnamI8*Bar HDMI\r\n", 7, "Bar HDMI")]
    public void HandleResponse_UpdatesInputName(string response, int index, string expectedName)
    {
        Respond(response);

        Assert.Equal(expectedName, _switcher.Inputs[index].Name);
    }

    [Theory]
    [InlineData("VnamO1*Codec Content", 0, "Codec Content")]
    [InlineData("VnamO2*Bench Output", 1, "Bench Output")]
    [InlineData("VnamO3*--", 2, "--")]
    public void HandleResponse_UpdatesOutputName(string response, int index, string expectedName)
    {
        Respond(response);

        Assert.Equal(expectedName, _switcher.Outputs[index].Name);
    }

    [Theory]
    [InlineData("VnamI9*Aux")]
    [InlineData("VnamO4*Nope")]
    [InlineData("VnamI0*Nope")]
    public void HandleResponse_IgnoresNamesForUnknownEndpoints(string response)
    {
        Respond(response);

        Assert.All(_switcher.Inputs, input => Assert.StartsWith("Input ", input.Name));
        Assert.DoesNotContain(_switcher.Outputs, output => output.Name == "Nope");
    }

    [Theory]
    [InlineData("HdcpI1*0", 0, ConnectionState.Disconnected, HdcpStatus.Unknown)]
    [InlineData("HdcpI2*1", 1, ConnectionState.Connected, HdcpStatus.NotSupported)]
    [InlineData("HdcpI2*2", 1, ConnectionState.Connected, HdcpStatus.Active)]
    [InlineData("HdcpI8*0\r\n", 7, ConnectionState.Disconnected, HdcpStatus.Unknown)]
    public void HandleResponse_SetsInputHdcpStatus(string response, int index, ConnectionState expectedConnection, HdcpStatus expectedHdcp)
    {
        Respond(response);

        Assert.Equal(expectedConnection, _switcher.Inputs[index].InputConnectionStatus);
        Assert.Equal(expectedHdcp, _switcher.Inputs[index].InputHdcpStatus);
        Assert.True(_switcher.Inputs[index].InUse);
    }

    [Theory]
    [InlineData("HdcpO1*1", 0, ConnectionState.Connected, HdcpStatus.NotSupported)]
    [InlineData("HdcpO1*2", 0, ConnectionState.Connected, HdcpStatus.Available)]
    [InlineData("HdcpO2*2", 1, ConnectionState.Connected, HdcpStatus.Available)]
    [InlineData("HdcpO3*0", 2, ConnectionState.Disconnected, HdcpStatus.Unknown)]
    public void HandleResponse_SetsOutputHdcpStatus(string response, int index, ConnectionState expectedConnection, HdcpStatus expectedHdcp)
    {
        Respond(response);

        Assert.Equal(expectedConnection, _switcher.Outputs[index].OutputConnectionStatus);
        Assert.Equal(expectedHdcp, _switcher.Outputs[index].OutputHdcpStatus);
        Assert.True(_switcher.Outputs[index].InUse);
    }

    [Fact]
    public void HandleResponse_IgnoresHdcpForUnknownEndpoints()
    {
        Respond("HdcpI9*2");
        Respond("HdcpO4*2");

        Assert.All(_switcher.Inputs, input => Assert.False(input.InUse));
        Assert.All(_switcher.Outputs, output => Assert.False(output.InUse));
    }

    [Theory]
    [InlineData("HplgO1*1", "1")]
    [InlineData("HplgO2*0", "2")]
    [InlineData("HplgO3*1", "3")]
    public void HandleResponse_RequestsOutputHdcpOnHotplug(string response, string outputNumber)
    {
        Respond(response);

        _mockClient.Verify(x => x.Send($"{EscapeHeader}O{outputNumber}HDCP\r"), Times.Once);
    }

    [Fact]
    public void HandleResponse_SetsInputSignalPresence()
    {
        Respond("In00 0*1*0*1*0*1*0*0");

        Assert.Equal(ConnectionState.Disconnected, _switcher.Inputs[0].InputConnectionStatus);
        Assert.Equal(ConnectionState.Connected, _switcher.Inputs[1].InputConnectionStatus);
        Assert.Equal(ConnectionState.Disconnected, _switcher.Inputs[2].InputConnectionStatus);
        Assert.Equal(ConnectionState.Connected, _switcher.Inputs[3].InputConnectionStatus);
        Assert.Equal(ConnectionState.Connected, _switcher.Inputs[5].InputConnectionStatus);
        Assert.Equal(ConnectionState.Disconnected, _switcher.Inputs[7].InputConnectionStatus);
    }

    [Fact]
    public void HandleResponse_AcceptsTheUnsolicitedSignalPresenceMessage()
    {
        Respond("IN00 1*0*0*0*0*0*0*0\r\n");

        Assert.Equal(ConnectionState.Connected, _switcher.Inputs[0].InputConnectionStatus);
        Assert.Equal(ConnectionState.Disconnected, _switcher.Inputs[1].InputConnectionStatus);
    }

    [Fact]
    public void HandleResponse_RequestsSignalPresenceOnReconfig()
    {
        Respond("Reconfig");

        _mockClient.Verify(x => x.Send($"{EscapeHeader}0LS\r"), Times.Once);
    }

    [Theory]
    [InlineData("In2*1 All", 0, "2")]
    [InlineData("In6*1 Vid", 0, "6")]
    [InlineData("In6*2 Vid\r\n", 1, "6")]
    [InlineData("In5*2 All", 1, "5")]
    public void HandleResponse_TracksTheVideoTieAsTheStreamAddress(string response, int index, string expectedAddress)
    {
        var handler = new Mock<AddressChangeHandler>();
        _switcher.Outputs[index].StreamChangeHandlers += handler.Object;

        Respond(response);

        Assert.Equal(expectedAddress, _switcher.Outputs[index].StreamAddress);
        handler.Verify(x => x.Invoke(expectedAddress), Times.Once);
    }

    [Fact]
    public void HandleResponse_DoesNotChangeTheStreamAddressForAudioTies()
    {
        Respond("In6*2 Vid");
        Respond("In1*2 Aud");

        Assert.Equal("6", _switcher.Outputs[1].StreamAddress);
    }

    [Fact]
    public void HandleResponse_IgnoresTiesForUnknownOutputs()
    {
        Respond("In1*3 All");
        Respond("In1*4 All");

        Assert.Equal(string.Empty, _switcher.Outputs[2].StreamAddress);
    }

    [Fact]
    public void HandleResponse_MarksAnUntiedOutput()
    {
        Respond("In6*1 All");
        Respond("Out1 In00 All");

        Assert.Equal(ExtronDtp2Cp82.UntiedAddress, _switcher.Outputs[0].StreamAddress);
    }

    [Fact]
    public void HandleResponse_MarksAllOutputsUntied()
    {
        Respond("In6*1 All");
        Respond("In6*2 All");
        Respond("In00 All");

        Assert.Equal(ExtronDtp2Cp82.UntiedAddress, _switcher.Outputs[0].StreamAddress);
        Assert.Equal(ExtronDtp2Cp82.UntiedAddress, _switcher.Outputs[1].StreamAddress);
        Assert.Equal(string.Empty, _switcher.Outputs[2].StreamAddress);
    }

    [Fact]
    public void HandleResponse_MarksOutputsUntiedWhenTheirInputIsUntied()
    {
        Respond("In6*1 All");
        Respond("In2*2 All");
        Respond("Out00 In6 All");

        Assert.Equal(ExtronDtp2Cp82.UntiedAddress, _switcher.Outputs[0].StreamAddress);
        Assert.Equal("2", _switcher.Outputs[1].StreamAddress);
    }

    [Fact]
    public void HandleResponse_TracksTheLoopOutTie()
    {
        Respond("Lout1");

        Assert.Equal("1", _switcher.Outputs[2].StreamAddress);
    }

    [Fact]
    public void HandleResponse_ProcessesEveryLineInAChunk()
    {
        Respond("HdcpI1*0\r\nHdcpI2*1\r\nIn6*2 Vid\r\n");

        Assert.Equal(ConnectionState.Disconnected, _switcher.Inputs[0].InputConnectionStatus);
        Assert.Equal(ConnectionState.Connected, _switcher.Inputs[1].InputConnectionStatus);
        Assert.Equal("6", _switcher.Outputs[1].StreamAddress);
    }

    [Theory]
    [InlineData("Vrb3")]
    [InlineData("Pti0*00030")]
    [InlineData("SsavT1*501")]
    [InlineData("E10")]
    [InlineData("E13")]
    [InlineData("Vtyp2*1")]
    [InlineData("")]
    public void HandleResponse_IgnoresUnrelatedResponses(string response)
    {
        Respond(response);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
        Assert.All(_switcher.Inputs, input => Assert.False(input.InUse));
        Assert.All(_switcher.Outputs, output => Assert.False(output.InUse));
    }

    [Fact]
    public void RouteAV_SendsTheCommand()
    {
        _switcher.RouteAV(6, 2);

        _mockClient.Verify(x => x.Send("6*2!"), Times.Once);
    }

    [Fact]
    public void RouteAV_RoutesToAllWithOutput0()
    {
        _switcher.RouteAV(3, 0);

        _mockClient.Verify(x => x.Send("3*!"), Times.Once);
    }

    [Fact]
    public void RouteAV_SendsOneTiePerOutput()
    {
        _switcher.RouteAV(1, [1, 2]);

        _mockClient.Verify(x => x.Send("1*1!"), Times.Once);
        _mockClient.Verify(x => x.Send("1*2!"), Times.Once);
        _mockClient.Verify(x => x.Send(It.Is<string>(s => s.Contains("+Q"))), Times.Never);
    }

    [Fact]
    public void RouteVideo_SendsTheCommand()
    {
        _switcher.RouteVideo(5, 2);

        _mockClient.Verify(x => x.Send("5*2%"), Times.Once);
    }

    [Fact]
    public void RouteVideo_RoutesToAllWithOutput0()
    {
        _switcher.RouteVideo(5, 0);

        _mockClient.Verify(x => x.Send("5*%"), Times.Once);
    }

    [Fact]
    public void RouteVideo_SendsOneTiePerOutput()
    {
        _switcher.RouteVideo(4, [1, 2]);

        _mockClient.Verify(x => x.Send("4*1%"), Times.Once);
        _mockClient.Verify(x => x.Send("4*2%"), Times.Once);
        _mockClient.Verify(x => x.Send(It.Is<string>(s => s.Contains("+Q"))), Times.Never);
    }

    [Fact]
    public void RouteAudio_SendsTheCommand()
    {
        _switcher.RouteAudio(1, 2);

        _mockClient.Verify(x => x.Send("1*2$"), Times.Once);
    }

    [Fact]
    public void RouteAudio_RoutesToAllWithOutput0()
    {
        _switcher.RouteAudio(1, 0);

        _mockClient.Verify(x => x.Send("1*$"), Times.Once);
    }

    [Fact]
    public void RouteAudio_SendsOneTiePerOutput()
    {
        _switcher.RouteAudio(8, [2, 1]);

        _mockClient.Verify(x => x.Send("8*2$"), Times.Once);
        _mockClient.Verify(x => x.Send("8*1$"), Times.Once);
        _mockClient.Verify(x => x.Send(It.Is<string>(s => s.Contains("+Q"))), Times.Never);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(9, 1)]
    [InlineData(1, 3)]
    [InlineData(1, 4)]
    [InlineData(1, -1)]
    public void Route_RejectsOutOfRangeTies(int input, int output)
    {
        _switcher.RouteAV(input, output);
        _switcher.RouteVideo(input, output);
        _switcher.RouteAudio(input, output);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void SetLoopOutInput_SendsTheCommand()
    {
        _switcher.SetLoopOutInput(6);

        _mockClient.Verify(x => x.Send($"{EscapeHeader}6LOUT\r"), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void SetLoopOutInput_RejectsOutOfRangeInputs(int input)
    {
        _switcher.SetLoopOutInput(input);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(500)]
    [InlineData(501)]
    public void SetSyncTimeout_SendsTheCommand(int seconds)
    {
        _switcher.SetSyncTimeout(seconds);

        _mockClient.Verify(x => x.Send($"{EscapeHeader}T1*{seconds}SSAV\r"), Times.Once);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(502)]
    public void SetSyncTimeout_IgnoresInvalidTimeouts(int seconds)
    {
        _switcher.SetSyncTimeout(seconds);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Capabilities_MatchTheSwitcher()
    {
        Assert.True(_switcher.RequiresOutputSpecification);
        Assert.True(_switcher.SupportsVideoBreakaway);
        Assert.True(_switcher.SupportsAudioBreakaway);
    }
}
