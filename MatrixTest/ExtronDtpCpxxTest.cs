using System.Collections.Concurrent;
using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Matrix.Tests;

[Collection("LoggerFactory")]
public class ExtronDtpCpxxTest
{
    private static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReturnTimeout = TimeSpan.FromSeconds(5);
    private readonly ExtronDtpCpxx _switcher;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();
    private readonly string EscapeHeader = "\x1b";

    public ExtronDtpCpxxTest()
    {
        _switcher = new ExtronDtpCpxx(_mockClient.Object, 8, "test matrix");
        _mockClient.Object.ResponseHandlers!.Invoke("Inf00*DTPCP108");
    }

    private static void SetConnection(Mock<CommunicationClient> client, ConnectionState state) => typeof(CommunicationClient)
        .GetProperty(nameof(CommunicationClient.ConnectionState))!.SetValue(client.Object, state);

    private static ConcurrentQueue<string> RecordSends(Mock<CommunicationClient> client, string heldCommand, ManualResetEventSlim release)
    {
        var sent = new ConcurrentQueue<string>();
        client.Setup(x => x.Send(It.IsAny<string>())).Callback<string>(command =>
        {
            sent.Enqueue(command);
            if (command == heldCommand)
                release.Wait(HoldTimeout);
        });
        return sent;
    }

    private static (ExtronDtpCpxx Switcher, ScopeCapturingLoggerFactory Logs) CreateWithCapturedLogs(Mock<CommunicationClient> client)
    {
        var logs = new ScopeCapturingLoggerFactory();
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = logs;
        try
        {
            return (new ExtronDtpCpxx(client.Object, 8, "Logged matrix"), logs);
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
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
        string input = "Foo";
        
        var method = _switcher.GetType().GetMethod("SendCommand", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(_switcher, [input]);

        Assert.Equal(CommunicationState.Okay, _switcher.CommunicationState);
    }

    [Fact]
    public void SendCommand_ReportsCommunicationHasFailed()
    {
        string input = "Foo";

        _mockClient.Setup(client => client.Send(It.IsAny<string>())).Throws(new IOException("Oh No!"));
        
        var method = _switcher.GetType().GetMethod("SendCommand", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(_switcher, [input]);

        Assert.Equal(CommunicationState.Error, _switcher.CommunicationState);
    }

    [Fact]
    public void RouteVideo_SendsTheCommand()
    {
        string expectedRouteCommand = "1*3%";
        _switcher.RouteVideo(1, 3);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void RouteVideo_RoutesToAllWithOutput0()
    {
        string expectedRouteCommand = "3*%";
        _switcher.RouteVideo(3, 0);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }
    
    [Fact]
    public void RouteVideo_HandlesAListOfOutputs()
    {
        string expectedRouteCommand = $"{EscapeHeader}+Q1*1%1*2%1*8%\r";
        _switcher.RouteVideo(1, [1, 2, 8]);
        
        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void RouteAudio_SendsTheCommand()
    {
        string expectedRouteCommand = "1*3$";
        _switcher.RouteAudio(1, 3);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void RouteAudio_RoutesToAllWithOutput0()
    {
        string expectedRouteCommand = "1*$";
        _switcher.RouteAudio(1, 0);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }
    
    [Fact]
    public void RouteAudio_HandlesAListOfOutputs()
    {
        string expectedRouteCommand = $"{EscapeHeader}+Q1*1$1*2$1*8$\r";
        _switcher.RouteAudio(1, [1, 2, 8]);
        
        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void RouteAV_SendsTheCommand()
    {
        string expectedRouteCommand = "1*3!";
        _switcher.RouteAV(1, 3);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void RouteAV_RoutesToAllWithOutput0()
    {
        string expectedRouteCommand = "1*!";
        _switcher.RouteAV(1, 0);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }
    
    [Fact]
    public void RouteAV_HandlesAListOfOutputs()
    {
        string expectedRouteCommand = $"{EscapeHeader}+Q1*1!1*2!1*8!\r";
        _switcher.RouteAV(1, [1, 2, 8]);
        
        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void SetSyncTimeout_SendsTheCommand()
    {
        string expectedCommand = "\u001bT3*0SSAV\r";
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
        string expectedCommand = "\u001bT1*501SSAV\r";
        _switcher.SetSyncTimeout(501, 1);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void SetSyncTimeout_StatesTheLimitItEnforces()
    {
        _switcher.SetSyncTimeout(502, 1);

        Assert.Contains(_switcher.Events,
            e => e.Type == EventType.Error && e.Info == "The sync timeout can't be longer than 501 seconds");
    }

    [Fact]
    public async Task Connect_EnablesVerboseModeThenRequestsInformationWithoutBlockingTheClient()
    {
        using var release = new ManualResetEventSlim();
        var sent = RecordSends(_mockClient, "\u001b3CV\r", release);

        var connect = Task.Run(() => SetConnection(_mockClient, ConnectionState.Connected));

        await connect.WaitAsync(ReturnTimeout);
        release.Set();
        await Wait.UntilAsync(() => sent.Count == 2, "The information request was never sent");
        Assert.Equal(["\u001b3CV\r", "I"], sent);
    }

    [Fact]
    public async Task Constructor_DoesNotBlockOnAnAlreadyConnectedClient()
    {
        var client = TestFactory.CreateCommunicationClient();
        SetConnection(client, ConnectionState.Connected);
        using var release = new ManualResetEventSlim();
        var sent = RecordSends(client, "\u001b3CV\r", release);

        var construct = Task.Run(() => new ExtronDtpCpxx(client.Object, 8, "Already connected"));

        await construct.WaitAsync(ReturnTimeout);
        release.Set();
        await Wait.UntilAsync(() => sent.Count == 2, "The information request was never sent");
        Assert.Equal(["\u001b3CV\r", "I"], sent);
    }

    [Fact]
    public async Task Connect_KeepsSendingAfterAnEarlierSequenceFails()
    {
        var client = TestFactory.CreateCommunicationClient();
        var switcher = new ExtronDtpCpxx(client.Object, 8, "fresh matrix");
        using var release = new ManualResetEventSlim(true);
        var sent = RecordSends(client, "", release);
        var changes = 0;
        switcher.CommunicationStateHandlers += _ =>
        {
            if (Interlocked.Increment(ref changes) <= 2)
                throw new InvalidOperationException("A subscriber failed");
        };

        SetConnection(client, ConnectionState.Connected);
        SetConnection(client, ConnectionState.Disconnected);
        SetConnection(client, ConnectionState.Connected);

        await Wait.UntilAsync(() => sent.Contains("I"), "The second connection sequence never finished");
        Assert.Equal(["\u001b3CV\r", "\u001b3CV\r", "I"], sent);
        Assert.Contains(switcher.Errors, error => error.Message == "The connection sequence failed");
    }

    [Fact]
    public void SupportsAudioBreakaway_IsTrueAsAudioTiesAreSent()
    {
        Assert.True(_switcher.SupportsAudioBreakaway);
    }

    [Fact]
    public void RouteToAListOfOutputs_NamesTheOutputsInTheEvent()
    {
        _switcher.RouteAV(1, [1, 2, 8]);
        _switcher.RouteVideo(2, [3, 4]);
        _switcher.RouteAudio(3, [5]);

        Assert.Contains(_switcher.Events, e => e.Info == "Switched outputs [1, 2, 8] to input 1");
        Assert.Contains(_switcher.Events, e => e.Info == "Switched video outputs [3, 4] to input 2");
        Assert.Contains(_switcher.Events, e => e.Info == "Switched audio output [5] to input 3");
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
        _switcher.Inputs[0].InputStatusChangedHandlers += mockSyncInfoHandler.Object;
        _mockClient.Object.ResponseHandlers!.Invoke(response);

        mockSyncInfoHandler.Verify(x => x.Invoke(expectedConnectionStatus, string.Empty, HdcpStatus.Unknown));
    }
    
    [Theory]
    [InlineData("Nmi1,Laptop\r", 0, "Laptop")]
    [InlineData("Nmi2,Wireless\r", 1, "Wireless")]
    [InlineData("Nmi3,BluRay\r", 2, "BluRay")]
    [InlineData("Nmi4,Lectern, left\r", 3, "Lectern, left")]
    public void HandleResponse_UpdatesInputName(string response, int index, string expectedName)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        Assert.Equal(expectedName, _switcher.Inputs[index].Name);
    }
    
    [Theory]
    [InlineData("Nmo1,Projector\r", 0, "Projector")]
    [InlineData("Nmo2,LCD\r", 1, "LCD")]
    [InlineData("Nmo3,Fountain\r", 2, "Fountain")]
    [InlineData("Nmo4,Stage, rear\r", 3, "Stage, rear")]
    public void HandleResponse_UpdatesOutputName(string response, int index, string expectedName)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        Assert.Equal(expectedName, _switcher.ComposedOutputs[index].Primary.Name);
        Assert.Equal($"{expectedName} - B", _switcher.ComposedOutputs[index].Secondary.Name);
    }

    [Theory]
    [InlineData("Inf00*DTPCP82", 8, 2)]
    [InlineData("Inf00*DTPCP84", 8, 4)]
    [InlineData("Inf00*DTPCP84 4K", 8, 4)]
    [InlineData("Inf00*DTPCP86", 8, 6)]
    [InlineData("Inf00*DTPCP108", 10, 8)]
    [InlineData("Inf00*DTPCP108 4K", 10, 8)]
    public void HandleResponse_SetsInputsAndOutputsFromModel(string response, int inputs, int outputs)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        Assert.Equal(inputs, _switcher.Inputs.Count);
        Assert.Equal(outputs, _switcher.ComposedOutputs.Count);
    }

    [Theory]
    [InlineData("Inf00*DTPCP8")]
    [InlineData("Inf00*DTPCP1080")]
    [InlineData("Inf00*DTPCP80")]
    [InlineData("Inf00*DTPCP08 4K")]
    [InlineData("Inf00*DTPCPx4")]
    public void ModelResponse_RaisesAnIssueForAnUnsupportedModel(string response)
    {
        var client = TestFactory.CreateCommunicationClient();
        var switcher = new ExtronDtpCpxx(client.Object, 8, "fresh matrix");

        var exception = Record.Exception(() => client.Object.ResponseHandlers!.Invoke(response));

        Assert.Null(exception);
        var issue = Assert.Single(switcher.GetOngoingIssues());
        Assert.Equal("model", issue.Key);
        Assert.Contains(response, issue.Message);
        Assert.Empty(switcher.Inputs);
    }

    [Fact]
    public void ModelResponse_ResolvesTheModelIssueWhenASupportedModelReports()
    {
        var client = TestFactory.CreateCommunicationClient();
        var switcher = new ExtronDtpCpxx(client.Object, 8, "fresh matrix");

        client.Object.ResponseHandlers!.Invoke("Inf00*DTPCP8");
        client.Object.ResponseHandlers!.Invoke("Inf00*DTPCP84 4K");

        Assert.Empty(switcher.GetOngoingIssues());
        Assert.Equal(8, switcher.Inputs.Count);
    }

    [Theory]
    [InlineData("Ityp01")]
    [InlineData("HdcpI01")]
    [InlineData("HdcpO2")]
    [InlineData("Nmi1Laptop")]
    [InlineData("Nmo1Projector")]
    [InlineData("HplgA")]
    [InlineData("HdcpI09*1")]
    [InlineData("HdcpO5*1")]
    public void MalformedResponse_IsLoggedWithTheRawValue(string response)
    {
        var client = TestFactory.CreateCommunicationClient();
        var (switcher, logs) = CreateWithCapturedLogs(client);
        client.Object.ResponseHandlers!.Invoke("Inf00*DTPCP84");

        client.Object.ResponseHandlers!.Invoke(response);

        Assert.Contains(logs.Logs, log => log.Message.Contains(response));
        Assert.All(switcher.Inputs, input => Assert.StartsWith("Input ", input.Name));
    }

    [Fact]
    public void ModelResponse_NotifiesSubscribersOnceThePortsExist()
    {
        var client = TestFactory.CreateCommunicationClient();
        var switcher = new ExtronDtpCpxx(client.Object, 8, "fresh matrix");
        var seen = new List<(int Inputs, int Outputs)>();
        switcher.EndpointsChangedHandlers += () => seen.Add((switcher.GetInputs().Count, switcher.NumberOfOutputs));

        client.Object.ResponseHandlers!.Invoke("Inf00*DTPCP84\r\n");

        Assert.Equal([(8, 4)], seen);
    }

    [Fact]
    public void SignalPresenceResponse_NotifiesSubscribersWhenTheInputCountChanges()
    {
        var seen = new List<int>();
        _switcher.EndpointsChangedHandlers += () => seen.Add(_switcher.GetInputs().Count);

        _mockClient.Object.ResponseHandlers!.Invoke("Frq00 00000000\r");

        Assert.Equal([8], seen);
    }

    private static ExtronDtpCpxx DiscoveredDtpCp84(Mock<CommunicationClient> client)
    {
        var switcher = new ExtronDtpCpxx(client.Object, 8, "Hearing Room 19.3 matrix");
        client.Object.ResponseHandlers!.Invoke("Inf00*DTPCP84 4K");
        client.Object.ResponseHandlers!.Invoke("Nmi1,Sharelink");
        client.Object.ResponseHandlers!.Invoke("Nmi2,Blu Ray");
        client.Object.ResponseHandlers!.Invoke("HdcpI01*2");
        client.Object.ResponseHandlers!.Invoke("HdcpI02*1");
        return switcher;
    }

    [Fact]
    public void SignalPresenceResponse_KeepsTheDiscoveredInputs()
    {
        var client = TestFactory.CreateCommunicationClient();
        var switcher = DiscoveredDtpCp84(client);
        var inputs = switcher.Inputs.ToList();
        var hdcp = switcher.Inputs.Select(input => input.InputHdcpStatus).ToList();

        client.Object.ResponseHandlers!.Invoke("Frq00 11101000");

        Assert.Equal(8, switcher.Inputs.Count);
        for (var index = 0; index < inputs.Count; index++)
            Assert.Same(inputs[index], switcher.Inputs[index]);
        Assert.Equal("Sharelink", switcher.Inputs[0].Name);
        Assert.Equal("Blu Ray", switcher.Inputs[1].Name);
        Assert.Equal(hdcp, switcher.Inputs.Select(input => input.InputHdcpStatus).ToList());
    }

    [Fact]
    public void SignalPresenceResponse_SetsEachInputsConnectionStatus()
    {
        var client = TestFactory.CreateCommunicationClient();
        var switcher = DiscoveredDtpCp84(client);

        client.Object.ResponseHandlers!.Invoke("Frq00 11101000");
        client.Object.ResponseHandlers!.Invoke("Frq00 11101100");

        Assert.Equal(
            [
                ConnectionState.Connected, ConnectionState.Connected, ConnectionState.Connected, ConnectionState.Disconnected,
                ConnectionState.Connected, ConnectionState.Connected, ConnectionState.Disconnected, ConnectionState.Disconnected
            ],
            switcher.Inputs.Select(input => input.InputConnectionStatus).ToList());
    }

    [Fact]
    public void SignalPresenceResponse_DoesNotNotifySubscribersWhenTheInputCountIsUnchanged()
    {
        var client = TestFactory.CreateCommunicationClient();
        var switcher = DiscoveredDtpCp84(client);
        var changes = 0;
        switcher.EndpointsChangedHandlers += () => changes++;

        client.Object.ResponseHandlers!.Invoke("Frq00 11101000");

        Assert.Equal(0, changes);
    }

    [Fact]
    public void ModelResponseOnReconnect_KeepsThePortsAndQueriesThemAgain()
    {
        var client = TestFactory.CreateCommunicationClient();
        var switcher = DiscoveredDtpCp84(client);
        var inputs = switcher.Inputs.ToList();
        var outputs = switcher.ComposedOutputs.ToList();
        var changes = 0;
        switcher.EndpointsChangedHandlers += () => changes++;
        client.Invocations.Clear();

        client.Object.ResponseHandlers!.Invoke("Inf00*DTPCP84 4K");

        Assert.Equal(0, changes);
        Assert.Equal(inputs, switcher.Inputs);
        Assert.Equal(outputs, switcher.ComposedOutputs);
        Assert.Equal("Sharelink", switcher.Inputs[0].Name);
        client.Verify(x => x.Send($"{EscapeHeader}1NI\r"), Times.Once);
        client.Verify(x => x.Send($"{EscapeHeader}I8HDCP\r"), Times.Once);
        client.Verify(x => x.Send($"{EscapeHeader}4NO\r"), Times.Once);
        client.Verify(x => x.Send($"{EscapeHeader}O4BHDCP\r"), Times.Once);
    }

    [Fact]
    public void OutputHdcpResponse_NotifiesSubscribersWhenAnOutputFirstReports()
    {
        var client = TestFactory.CreateCommunicationClient();
        var switcher = DiscoveredDtpCp84(client);
        var seen = new List<int>();
        switcher.EndpointsChangedHandlers += () => seen.Add(switcher.GetOutputs().Count);

        client.Object.ResponseHandlers!.Invoke("HdcpO3A*0");
        client.Object.ResponseHandlers!.Invoke("HdcpO3B*1");
        client.Object.ResponseHandlers!.Invoke("HdcpO3B*0");

        Assert.Equal([1, 2], seen);
    }

    [Fact]
    public void InputTypeResponse_RequestsThatInputsHdcpStatus()
    {
        _mockClient.Invocations.Clear();

        _mockClient.Object.ResponseHandlers!.Invoke("Ityp05*2");

        _mockClient.Verify(x => x.Send($"{EscapeHeader}I5HDCP\r"), Times.Once);
    }

    [Fact]
    public void Inputs_UseTheirNumberAsStreamAddress()
    {
        Assert.Equal(Enumerable.Range(1, 10).Select(number => number.ToString()),
            _switcher.Inputs.Select(input => input.StreamAddress));
    }

    [Theory]
    [InlineData("Out3 In3 Vid", 3, "3")]
    [InlineData("Out4 In1 All", 4, "1")]
    [InlineData("Out2 In7 Vid", 2, "7")]
    public void VideoTieResponse_SetsTheOutputsSourceOnBothHalves(string response, int output, string expectedAddress)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);

        Assert.Equal(expectedAddress, _switcher.ComposedOutputs[output - 1].Primary.StreamAddress);
        Assert.Equal(expectedAddress, _switcher.ComposedOutputs[output - 1].Secondary.StreamAddress);
    }

    [Fact]
    public void AudioTieResponse_LeavesTheVideoSourceAlone()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("Out4 In1 All");
        _mockClient.Object.ResponseHandlers!.Invoke("Out1 In0 Aud");
        _mockClient.Object.ResponseHandlers!.Invoke("Out4 In2 Aud");

        Assert.Equal("1", _switcher.ComposedOutputs[3].Primary.StreamAddress);
        Assert.Equal(string.Empty, _switcher.ComposedOutputs[0].Primary.StreamAddress);
    }

    [Fact]
    public void UntieResponse_ClearsTheOutputsSource()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("Out4 In1 All");
        _mockClient.Object.ResponseHandlers!.Invoke("Out4 In0 All");

        Assert.Equal(string.Empty, _switcher.ComposedOutputs[3].Primary.StreamAddress);
        Assert.Equal(string.Empty, _switcher.ComposedOutputs[3].Secondary.StreamAddress);
    }

    [Fact]
    public void ClearAllTiesResponse_UntiesEveryOutput()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("Out3 In3 Vid");
        _mockClient.Object.ResponseHandlers!.Invoke("Out4 In1 All");
        _mockClient.Object.ResponseHandlers!.Invoke("Out2 In7 Vid");

        _mockClient.Object.ResponseHandlers!.Invoke("In0 All");

        Assert.All(_switcher.ComposedOutputs, output =>
        {
            Assert.Equal(string.Empty, output.Primary.StreamAddress);
            Assert.Equal(string.Empty, output.Secondary.StreamAddress);
        });
    }

    [Fact]
    public void TieResponseBeforeTheModelIsKnown_IsIgnored()
    {
        var client = TestFactory.CreateCommunicationClient();
        var switcher = new ExtronDtpCpxx(client.Object, 8, "fresh matrix");

        var exception = Record.Exception(() => client.Object.ResponseHandlers!.Invoke("Out3 In3 Vid"));

        Assert.Null(exception);
        Assert.Empty(switcher.ComposedOutputs);
    }

    [Fact]
    public void ModelResponse_QueriesEachOutputsVideoTie()
    {
        var client = TestFactory.CreateCommunicationClient();
        _ = DiscoveredDtpCp84(client);

        for (var output = 1; output <= 4; output++)
            client.Verify(x => x.Send($"{output}%"), Times.Once);
    }

    [Fact]
    public void QuickTieResponse_QueriesTheVideoTiesAgain()
    {
        _mockClient.Invocations.Clear();

        _mockClient.Object.ResponseHandlers!.Invoke("Qik");

        for (var output = 1; output <= 8; output++)
            _mockClient.Verify(x => x.Send($"{output}%"), Times.Once);
    }

    [Theory]
    [InlineData("Hplg01", "1")]
    [InlineData("Hplg02", "2")]
    [InlineData("Hplg03", "3")]
    [InlineData("Hplg05A", "5A")]
    [InlineData("Hplg05B", "5B")]
    [InlineData("Hplg10", "10")]
    [InlineData("Hplg12A", "12A")]
    public void HandleResponse_RequestsOutputFormatOnHotplugEvent(string eventResponse, string outputNumber)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(eventResponse);
        _mockClient.Verify(x => x.Send($"{EscapeHeader}O{outputNumber}HDCP\r"), Times.AtLeastOnce);
    }

    [Theory]
    [InlineData("HdcpI01*2", 0, ConnectionState.Connected, HdcpStatus.NotSupported)]
    [InlineData("HdcpI02*1", 1, ConnectionState.Connected, HdcpStatus.Active)]
    [InlineData("HdcpI03*2", 2, ConnectionState.Connected, HdcpStatus.NotSupported)]
    [InlineData("HdcpI04*0", 3, ConnectionState.Disconnected, HdcpStatus.Unknown)]
    [InlineData("HdcpI08*0", 7, ConnectionState.Disconnected, HdcpStatus.Unknown)]
    [InlineData("HdcpI10*1", 9, ConnectionState.Connected, HdcpStatus.Active)]
    [InlineData("HdcpI10*2", 9, ConnectionState.Connected, HdcpStatus.NotSupported)]
    public void InputHdcpResponse_SetsTheInputsConnectionAndHdcpStatus(string eventResponse, int arrayIndex, ConnectionState expected, HdcpStatus expectedHdcpStatus)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(eventResponse);
        Assert.Equal(expected, _switcher.Inputs[arrayIndex].InputConnectionStatus);
        Assert.Equal(expectedHdcpStatus, _switcher.Inputs[arrayIndex].InputHdcpStatus);
        Assert.True(_switcher.Inputs[arrayIndex].InUse);
    }

    [Fact]
    public void HearingRoom19_3HdcpResponses_ShowTheBluRayAsTheOnlyHdcpSource()
    {
        var client = TestFactory.CreateCommunicationClient();
        var switcher = DiscoveredDtpCp84(client);

        foreach (var response in new[]
                 {
                     "HdcpI03*2", "HdcpI04*0", "HdcpI05*0", "HdcpI06*0", "HdcpI07*0", "HdcpI08*0",
                     "HdcpO1*0", "HdcpO2*3", "HdcpO3A*0", "HdcpO3B*1", "HdcpO4A*1", "HdcpO4B*0"
                 })
            client.Object.ResponseHandlers!.Invoke(response);

        Assert.Equal(
            [
                HdcpStatus.NotSupported, HdcpStatus.Active, HdcpStatus.NotSupported, HdcpStatus.Unknown,
                HdcpStatus.Unknown, HdcpStatus.Unknown, HdcpStatus.Unknown, HdcpStatus.Unknown
            ],
            switcher.Inputs.Select(input => input.InputHdcpStatus).ToList());
        Assert.Equal(
            [
                HdcpStatus.Unknown, HdcpStatus.Active, HdcpStatus.Unknown, HdcpStatus.NotSupported,
                HdcpStatus.NotSupported, HdcpStatus.Unknown
            ],
            switcher.Outputs.Select(output => output.OutputHdcpStatus).ToList());
    }

    [Theory]
    [InlineData("HdcpO1*0", 0, ConnectionState.Disconnected, HdcpStatus.Unknown)]
    [InlineData("HdcpO2*0", 1, ConnectionState.Disconnected, HdcpStatus.Unknown)]
    [InlineData("HdcpO2*1", 1, ConnectionState.Connected, HdcpStatus.NotSupported)]
    [InlineData("HdcpO2*2", 1, ConnectionState.Connected, HdcpStatus.Available)]
    [InlineData("HdcpO2*3", 1, ConnectionState.Connected, HdcpStatus.Active)]
    public void HandleResponse_SetsOutputConnectionStatusForSingleOutputNumbers(string eventResponse, int arrayIndex, ConnectionState expected, HdcpStatus expectedHdcpStatus)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(eventResponse);
        Assert.Equal(expected, _switcher.ComposedOutputs[arrayIndex].Primary.OutputConnectionStatus);
        Assert.Equal(expectedHdcpStatus, _switcher.ComposedOutputs[arrayIndex].Primary.OutputHdcpStatus);
        Assert.True(_switcher.ComposedOutputs[arrayIndex].Primary.InUse);
    }

    [Theory]
    [InlineData("HdcpO3A*0", 2, ConnectionState.Disconnected, HdcpStatus.Unknown)]
    [InlineData("HdcpO4A*0", 3, ConnectionState.Disconnected, HdcpStatus.Unknown)]
    [InlineData("HdcpO4A*1", 3, ConnectionState.Connected, HdcpStatus.NotSupported)]
    [InlineData("HdcpO6A*2", 5, ConnectionState.Connected, HdcpStatus.Available)]
    public void HandleResponse_SetsOutputConnectionStatusForSplitOutputs_Primary(string eventResponse, int arrayIndex, ConnectionState expected, HdcpStatus expectedHdcpStatus)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(eventResponse);
        Assert.Equal(expected, _switcher.ComposedOutputs[arrayIndex].Primary.OutputConnectionStatus);
        Assert.Equal(expectedHdcpStatus, _switcher.ComposedOutputs[arrayIndex].Primary.OutputHdcpStatus);
        Assert.True(_switcher.ComposedOutputs[arrayIndex].Primary.InUse);
    }

    [Theory]
    [InlineData("HdcpO3B*1", 2, ConnectionState.Connected, HdcpStatus.NotSupported)]
    [InlineData("HdcpO4B*0", 3, ConnectionState.Disconnected, HdcpStatus.Unknown)]
    [InlineData("HdcpO6B*3", 5, ConnectionState.Connected, HdcpStatus.Active)]
    public void HandleResponse_SetsOutputConnectionStatusForSplitOutputs_Secondary(string eventResponse, int arrayIndex, ConnectionState expected, HdcpStatus expectedHdcpStatus)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(eventResponse);
        Assert.Equal(expected, _switcher.ComposedOutputs[arrayIndex].Secondary.OutputConnectionStatus);
        Assert.Equal(expectedHdcpStatus, _switcher.ComposedOutputs[arrayIndex].Secondary.OutputHdcpStatus);
        Assert.True(_switcher.ComposedOutputs[arrayIndex].Secondary.InUse);
    }
}