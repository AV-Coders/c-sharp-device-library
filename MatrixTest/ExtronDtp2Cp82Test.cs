using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AVCoders.Matrix.Tests;

public class ExtronDtp2Cp82Test
{
    private static readonly TimeSpan QueryReplyTimeout = TimeSpan.FromSeconds(5);
    private static readonly string[] IdentityQueries = ["*Q", "N", "\u001bCN\r", "\u001bCH\r"];
    private const string TemperatureQuery = "\u001b20STAT\r";
    private readonly FakeTimeProvider _time = new();
    private readonly ExtronDtp2Cp82 _switcher;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();
    private const string EscapeHeader = "\x1b";
    private const string ModelResponse = "Inf00*DTP2 CrossPoint 82";

    public ExtronDtp2Cp82Test()
    {
        _switcher = new ExtronDtp2Cp82(_mockClient.Object, "test matrix", _time);
    }

    private void Respond(string response) => _mockClient.Object.ResponseHandlers!.Invoke(response);

    [Fact]
    public void FailedSend_ReportsCommunicationHasFailed()
    {
        _mockClient.Setup(client => client.Send(It.IsAny<string>())).Throws(new IOException("Oh No!"));

        _switcher.RouteAV(1, 1);

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
    public void HandleResponse_GivesAnUntiedOutputTheAddressZero()
    {
        Respond("In6*1 All");
        Respond("Out1 In00 All");

        Assert.Equal("0", _switcher.Outputs[0].StreamAddress);
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
    public void HandleResponse_MatchesAnUntiedInputGivenWithALeadingZero()
    {
        Respond("In6*1 All");
        Respond("In2*2 All");
        Respond("Out00 In06 All");

        Assert.Equal(ExtronDtp2Cp82.UntiedAddress, _switcher.Outputs[0].StreamAddress);
        Assert.Equal("2", _switcher.Outputs[1].StreamAddress);
    }

    [Fact]
    public void HandleResponse_RaisesAnIssueWhenTheSwitcherRejectsACommand()
    {
        _switcher.RouteAV(2, 1);

        Respond("E10");

        Assert.Contains(_switcher.GetIssues(),
            issue => issue.Key == "rejected-command" && issue.Status == IssueStatus.Momentary && issue.Message.Contains("E10"));
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
    public void RouteAV_SendsTheCommandAndReportsCommunicationIsOkay()
    {
        _switcher.RouteAV(6, 2);

        _mockClient.Verify(x => x.Send("6*2!"), Times.Once);
        Assert.Equal(CommunicationState.Okay, _switcher.CommunicationState);
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

    private DeviceDetail? Detail(string label) => _switcher.Details.SingleOrDefault(detail => detail.Label == label);

    private void SendPoll() => typeof(ExtronDtp2Cp82)
        .GetMethod("SendPoll", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(_switcher, []);

    private List<string> RecordSends()
    {
        var sent = new List<string>();
        _mockClient.Setup(x => x.Send(It.IsAny<string>())).Callback<string>(sent.Add);
        return sent;
    }

    [Theory]
    [InlineData(ModelResponse, "DTP2 CrossPoint 82")]
    [InlineData("Inf00*DTP2 CrossPoint 82 IPCP SA", "DTP2 CrossPoint 82 IPCP SA")]
    [InlineData("Inf00*DTPCP84 4K", "DTPCP84 4K")]
    public void ModelResponse_ShowsTheModel(string response, string expectedModel)
    {
        Respond(response);

        Assert.Equal(new DeviceDetail("Model", expectedModel), Detail("Model"));
    }

    [Fact]
    public void ModelResponse_QueriesTheStatusBeforeReadingTheIdentity()
    {
        var sent = RecordSends();

        Respond(ModelResponse);

        Assert.Equal($"{EscapeHeader}I1VNAM\r", sent[0]);
        Assert.Equal([$"{EscapeHeader}0LS\r", ..IdentityQueries], sent.TakeLast(5));
    }

    [Fact]
    public void UnexpectedModel_DoesNotReadTheIdentity()
    {
        Respond("Inf00*DTPCP84 4K");

        foreach (var query in IdentityQueries)
            _mockClient.Verify(client => client.Send(query), Times.Never);
    }

    [Fact]
    public void IdentityReplies_AreShownAsReceived()
    {
        Respond(ModelResponse);

        Respond("Bld1.04.0001\r\nPno60-1608-01\r\nIpn DTP2-CP-82-12-34-56\r\nIph 00-05-A6-12-34-56\r\n");

        Assert.Equal("1.04.0001", Detail("Firmware")?.Value);
        Assert.Equal("60-1608-01", Detail("Part Number")?.Value);
        Assert.Equal("DTP2-CP-82-12-34-56", Detail("Unit Name")?.Value);
        Assert.Equal("00-05-A6-12-34-56", Detail("MAC")?.Value);
        Assert.Empty(_switcher.GetOngoingIssues());
    }

    [Fact]
    public void RejectedIdentityQuery_ShowsItIsNotSupportedWithNoIssue()
    {
        Respond(ModelResponse);
        foreach (var reply in ExtronBenchCaptures.Dtp2Cp82StatusReplies)
            Respond(reply);
        Respond("Bld1.04.0001");
        Respond("Pno60-1608-01");
        var communicationState = _switcher.CommunicationState;

        Respond("E10");

        Assert.Equal(new DeviceDetail("Unit Name", "Not supported: E10", DetailTone.Normal), Detail("Unit Name"));
        Assert.Empty(_switcher.GetIssues());
        Assert.Equal(communicationState, _switcher.CommunicationState);
    }

    [Fact]
    public void SendPoll_ReadsTheTemperatureAfterTheSignalPresence()
    {
        var sent = RecordSends();

        SendPoll();

        Assert.Equal([$"{EscapeHeader}0LS\r", TemperatureQuery], sent);
    }

    [Fact]
    public void RejectedTemperatureQuery_IsNotSentAgainUntilTheSwitcherReconnects()
    {
        SendPoll();

        Respond(ExtronBenchCaptures.Dtp2Cp82SignalPresenceReply);
        Respond("E13");

        Assert.Equal(new DeviceDetail("Temperature", "Not supported: E13", DetailTone.Normal), Detail("Temperature"));
        Assert.Empty(_switcher.GetIssues());
        _mockClient.Invocations.Clear();
        SendPoll();
        _mockClient.Verify(client => client.Send(TemperatureQuery), Times.Never);

        _mockClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Disconnected);
        SendPoll();
        _mockClient.Verify(client => client.Send(TemperatureQuery), Times.Once);
    }

    [Fact]
    public void BusyTemperatureQuery_IsSentAgainAndRaisesTheReadIssueOnTheThirdInARow()
    {
        SendPoll();
        Respond(ExtronBenchCaptures.Dtp2Cp82SignalPresenceReply);
        Respond("E22\r\n");
        SendPoll();
        Respond(ExtronBenchCaptures.Dtp2Cp82SignalPresenceReply);
        Respond("E22\r\n");

        Assert.Empty(_switcher.GetIssues());
        _mockClient.Verify(client => client.Send(TemperatureQuery), Times.Exactly(2));

        SendPoll();
        Respond(ExtronBenchCaptures.Dtp2Cp82SignalPresenceReply);
        Respond("E22\r\n");

        var issue = Assert.Single(_switcher.GetOngoingIssues());
        Assert.Equal("temperature-read", issue.Key);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Equal("The switcher did not answer Esc 20STAT (Temperature) 3 times in a row, the last was E22", issue.Message);
        Respond("20Stat 41");
        Assert.Empty(_switcher.GetOngoingIssues());
        Assert.Equal(new DeviceDetail("Temperature", "41"), Detail("Temperature"));
    }

    [Fact]
    public void UnansweredIdentityQueries_AreSentAgainAndRaiseTheReadIssueOnTheThirdMiss()
    {
        Respond(ModelResponse);
        _time.Advance(QueryReplyTimeout);
        SendPoll();
        _time.Advance(QueryReplyTimeout);
        SendPoll();

        Assert.Empty(_switcher.GetIssues());
        foreach (var query in IdentityQueries)
            _mockClient.Verify(client => client.Send(query), Times.Exactly(3));

        _time.Advance(QueryReplyTimeout);
        SendPoll();

        Assert.Equal(["firmware-read", "part-number-read", "unit-name-read", "mac-read"],
            _switcher.GetOngoingIssues().Select(issue => issue.Key));
        Assert.Contains(_switcher.GetOngoingIssues(),
            issue => issue.Message == "The switcher did not answer *Q (Firmware) 3 times in a row, the last was no Bld reply within 5 s");
        Respond("Bld1.04.0001");
        Assert.DoesNotContain(_switcher.GetOngoingIssues(), issue => issue.Key == "firmware-read");
    }

    [Fact]
    public void UnansweredQueries_WaitForTheReplyTimeout()
    {
        Respond(ModelResponse);

        _time.Advance(QueryReplyTimeout - TimeSpan.FromTicks(1));
        SendPoll();

        Assert.Empty(_switcher.GetOngoingIssues());
        foreach (var query in IdentityQueries)
            _mockClient.Verify(client => client.Send(query), Times.Once);
    }

    [Fact]
    public void Disconnect_ForgetsTheOutstandingQueries()
    {
        Respond(ModelResponse);
        _mockClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Disconnected);

        Respond("E10");

        Assert.Null(Detail("Firmware"));
        Assert.Empty(_switcher.GetIssues());
    }
}

[Collection(LoggerFactoryCollection.Name)]
public class ExtronDtp2Cp82LogTest
{
    private readonly Mock<CommunicationClient> _client = TestFactory.CreateCommunicationClient();
    private readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly ExtronDtp2Cp82 _switcher;

    public ExtronDtp2Cp82LogTest()
    {
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = _logs;
        try
        {
            _switcher = new ExtronDtp2Cp82(_client.Object, "Logged matrix");
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }

    private void Respond(string response) => _client.Object.ResponseHandlers!.Invoke(response);

    [Fact]
    public void HandleResponse_LogsAnUnexpectedModelOnce()
    {
        Respond("Inf00*DTPCP84 4K");

        Assert.Single(_logs.Logs, log => log.Message.StartsWith("Expected a DTP2 CrossPoint 82 but the switcher reported DTPCP84 4K"));
    }

    [Theory]
    [InlineData("VnamI*Nope")]
    [InlineData("VnamX1*Nope")]
    public void HandleResponse_LogsAMalformedName(string response)
    {
        Respond(response);

        Assert.Contains(_logs.Logs, log => log.Message.Contains(response));
        Assert.All(_switcher.Inputs, input => Assert.StartsWith("Input ", input.Name));
    }

    [Theory]
    [InlineData("In00 1*1*1")]
    [InlineData("In00 1*1*1*1*1*1*1*1*1")]
    public void HandleResponse_LogsAndIgnoresSignalPresenceThatDoesNotListEveryInput(string response)
    {
        Respond(response);

        Assert.Contains(_logs.Logs, log => log.Message.Contains(response[5..]) && log.Message.Contains("8 inputs"));
        Assert.All(_switcher.Inputs, input => Assert.False(input.InUse));
    }
}

[Collection(LoggerFactoryCollection.Name)]
public class ExtronDtp2Cp82BenchTest
{
    private static readonly TimeSpan ConnectStepGap = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);
    private const string RouteTrigger = "5d30414a";
    private readonly FakeTimeProvider _time = new();
    private readonly Mock<TcpClient> _client = TestFactory.CreateTcpClient();
    private readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly ExtronDtp2Cp82 _switcher;

    public ExtronDtp2Cp82BenchTest()
    {
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = _logs;
        try
        {
            _switcher = new ExtronDtp2Cp82(_client.Object, "Bench matrix", _time);
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }

    private void Respond(IEnumerable<string> replies)
    {
        foreach (var reply in replies)
            _client.Object.ResponseHandlers!.Invoke(reply);
    }

    private async Task Connect()
    {
        typeof(CommunicationClient).GetProperty(nameof(CommunicationClient.ConnectionState))!
            .SetValue(_client.Object, ConnectionState.Connected);
        Respond([ExtronBenchCaptures.Dtp2Cp82Banner]);
        await Wait.UntilAsync(_time, ConnectStepGap,
            () => _client.Invocations.Any(invocation => Equals(invocation.Arguments[0], "I")), "The model was never asked for");
        Respond([..ExtronBenchCaptures.Dtp2Cp82ConnectReplies, ..ExtronBenchCaptures.Dtp2Cp82StatusReplies, ..ExtronBenchCaptures.Dtp2Cp82IdentityReplies]);
    }

    private void SendPoll() => typeof(ExtronDtp2Cp82)
        .GetMethod("SendPoll", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(_switcher, []);

    private DeviceDetail? Detail(string label) => _switcher.Details.SingleOrDefault(detail => detail.Label == label);

    [Fact]
    public async Task BenchSequence_MatchesEveryReplyToTheCommandItAnswers()
    {
        await Connect();
        SendPoll();
        Respond([ExtronBenchCaptures.Dtp2Cp82SignalPresenceReply, ExtronBenchCaptures.Dtp2Cp82TemperatureReply]);
        _time.Advance(ReplyTimeout);

        SendPoll();

        Assert.Equal(["DTP2 CrossPoint 82", "1.04.0004", "60-1812-01", "ROOM-VP01", "00-05-A6-00-00-03", "34"],
            new[] { "Model", "Firmware", "Part Number", "Unit Name", "MAC", "Temperature" }.Select(label => Detail(label)?.Value));
        Assert.Equal(["1", "6", "1"], _switcher.Outputs.Select(output => output.StreamAddress));
        Assert.Equal("Members PC", _switcher.Inputs[5].Name);
        Assert.DoesNotContain(_logs.Logs, log => log.Message.StartsWith("The switcher didn't answer"));
        Assert.DoesNotContain(_logs.Logs, log => log.Message.Contains("asking again"));
        Assert.DoesNotContain(_logs.Logs, log => log.Level >= LogLevel.Warning);
        Assert.Empty(_switcher.GetIssues());
    }

    [Fact]
    public async Task RejectedRoute_WhileTheTemperatureQueryIsPending_RaisesTheRejectionIssueAndTheQueryKeepsItsReply()
    {
        await Connect();
        using (Trigger.Resume(RouteTrigger))
            _switcher.RouteVideo(3, 2);
        SendPoll();

        Respond(["E13\r\n", ExtronBenchCaptures.Dtp2Cp82SignalPresenceReply, ExtronBenchCaptures.Dtp2Cp82TemperatureReply]);

        var issue = Assert.Single(_switcher.GetIssues());
        Assert.Equal(("rejected-command", IssueStatus.Momentary, "The switcher rejected 3*2% with E13"),
            (issue.Key, issue.Status, issue.Message));
        Assert.Equal(RouteTrigger, Assert.Single(_logs.Logs, log => log.Message == issue.Message).TriggerId);
        Assert.Equal(new DeviceDetail("Temperature", "34"), Detail("Temperature"));
    }

    [Fact]
    public async Task TieFromTheRoomProcessor_WhileOurRouteAwaitsItsReply_LeavesTheRejectionToOurRoute()
    {
        await Connect();
        using (Trigger.Resume(RouteTrigger))
            _switcher.RouteAV(3, 1);
        SendPoll();

        Respond(["In5*2 All\r\n", "E13\r\n", ExtronBenchCaptures.Dtp2Cp82SignalPresenceReply, ExtronBenchCaptures.Dtp2Cp82TemperatureReply]);

        Assert.Equal("5", _switcher.Outputs[1].StreamAddress);
        var issue = Assert.Single(_switcher.GetIssues());
        Assert.Equal("rejected-command", issue.Key);
        Assert.Equal(RouteTrigger, Assert.Single(_logs.Logs, log => log.Message == issue.Message).TriggerId);
        Assert.Equal(new DeviceDetail("Temperature", "34"), Detail("Temperature"));
    }
}
