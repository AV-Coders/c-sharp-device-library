using System.Collections.Concurrent;
using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AVCoders.Matrix.Tests;

public class ExtronDtpCpxxTest
{
    private static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReturnTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectStepGap = TimeSpan.FromMilliseconds(200);
    private readonly FakeTimeProvider _time = new();
    private readonly ExtronDtpCpxx _switcher;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();
    private readonly string EscapeHeader = "\x1b";

    public ExtronDtpCpxxTest()
    {
        _switcher = new ExtronDtpCpxx(_mockClient.Object, 8, "test matrix", _time);
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

    [Fact]
    public void FailedSend_ReportsCommunicationHasFailed()
    {
        _mockClient.Setup(client => client.Send(It.IsAny<string>())).Throws(new IOException("Oh No!"));

        _switcher.RouteAV(1, 3);

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
    public void RouteAV_SendsTheCommandAndReportsCommunicationIsOkay()
    {
        string expectedRouteCommand = "1*3!";
        _switcher.RouteAV(1, 3);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
        Assert.Equal(CommunicationState.Okay, _switcher.CommunicationState);
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
    public void SetSyncTimeout_RefusesALongerTimeoutAndStatesTheLimit()
    {
        _switcher.SetSyncTimeout(502, 1);

        _mockClient.Verify(x => x.Send(It.Is<string>(s => s.Contains("SSAV"))), Times.Never);
        Assert.Contains(_switcher.Events,
            e => e.Type == EventType.Error && e.Info == "The sync timeout can't be longer than 501 seconds");
    }

    [Fact]
    public void SetSyncTimeout_SetsToNeverDropSync()
    {
        string expectedCommand = "\u001bT1*501SSAV\r";
        _switcher.SetSyncTimeout(501, 1);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public async Task Connect_EnablesVerboseModeThenRequestsInformationWithoutBlockingTheClient()
    {
        using var release = new ManualResetEventSlim();
        var sent = RecordSends(_mockClient, "\u001b3CV\r", release);

        var connect = Task.Run(() => SetConnection(_mockClient, ConnectionState.Connected));

        await connect.WaitAsync(ReturnTimeout);
        release.Set();
        await Wait.UntilAsync(_time, ConnectStepGap, () => sent.Count == 2, "The information request was never sent");
        Assert.Equal(["\u001b3CV\r", "I"], sent);
    }

    [Fact]
    public async Task Constructor_DoesNotBlockOnAnAlreadyConnectedClient()
    {
        var client = TestFactory.CreateCommunicationClient();
        SetConnection(client, ConnectionState.Connected);
        using var release = new ManualResetEventSlim();
        var sent = RecordSends(client, "\u001b3CV\r", release);

        var construct = Task.Run(() => new ExtronDtpCpxx(client.Object, 8, "Already connected", _time));

        await construct.WaitAsync(ReturnTimeout);
        release.Set();
        await Wait.UntilAsync(_time, ConnectStepGap, () => sent.Count == 2, "The information request was never sent");
        Assert.Equal(["\u001b3CV\r", "I"], sent);
    }

    [Fact]
    public async Task Connect_KeepsSendingAfterAnEarlierSequenceFails()
    {
        var client = TestFactory.CreateCommunicationClient();
        var switcher = new ExtronDtpCpxx(client.Object, 8, "fresh matrix", _time);
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

        await Wait.UntilAsync(_time, ConnectStepGap, () => sent.Contains("I"), "The second connection sequence never finished");
        Assert.Equal(["\u001b3CV\r", "\u001b3CV\r", "I"], sent);
        Assert.Contains(switcher.Errors, error => error.Message == "The connection sequence failed");
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

    [Theory]
    [InlineData("Inf00*DTPCP84 4K", "DTPCP84 4K")]
    [InlineData("Inf00*DTPCPx4", "DTPCPx4")]
    public void ModelResponse_ShowsTheModelAsReceived(string response, string expectedModel)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);

        Assert.Equal(new DeviceDetail("Model", expectedModel), _switcher.Details.Single(detail => detail.Label == "Model"));
    }
}

[Collection(LoggerFactoryCollection.Name)]
public class ExtronDtpCpxxLogTest
{
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
        var logs = new ScopeCapturingLoggerFactory();
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = logs;
        ExtronDtpCpxx switcher;
        try
        {
            switcher = new ExtronDtpCpxx(client.Object, 8, "Logged matrix");
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
        client.Object.ResponseHandlers!.Invoke("Inf00*DTPCP84");

        client.Object.ResponseHandlers!.Invoke(response);

        Assert.Contains(logs.Logs, log => log.Message.Contains(response));
        Assert.All(switcher.Inputs, input => Assert.StartsWith("Input ", input.Name));
    }
}

[Collection(LoggerFactoryCollection.Name)]
public class ExtronDtpCpxxBenchTest
{
    private static readonly TimeSpan ConnectStepGap = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);
    private const string RouteTrigger = "5d30414a";
    private const string SystemStatusQuery = "S";
    private static readonly string[] IdentityQueries = ["*Q", "N", "\u001bCN\r", "\u001bCH\r"];
    private static readonly string[] IdentityReplies = ["Bld2.04.0003", "Pno60-1381-01", "Ipn ROOM-VID-MTX01", "Iph 00-05-A6-00-00-04"];
    private static readonly string[] IdentityValues = ["2.04.0003", "60-1381-01", "ROOM-VID-MTX01", "00-05-A6-00-00-04"];
    private const string SystemStatusReply = "Sts00*12.250 35.000 1976 2004";
    private static readonly string[] ReadLabels = ["Firmware Build", "Part Number", "Unit Name", "MAC", "System Status"];
    private static readonly string[] HdcpProbesWithNoOutput =
    [
        "Esc O1AHDCP", "Esc O1BHDCP", "Esc O2AHDCP", "Esc O2BHDCP", "Esc O3AHDCP", "Esc O3BHDCP", "Esc O4AHDCP", "Esc O4BHDCP",
        "Esc O5HDCP", "Esc O6HDCP", "Esc O7AHDCP", "Esc O7BHDCP", "Esc O8AHDCP", "Esc O8BHDCP"
    ];
    private readonly FakeTimeProvider _time = new();
    private readonly Mock<SshClient> _client = TestFactory.CreateSshClient();
    private readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly ExtronDtpCpxx _switcher;

    public ExtronDtpCpxxBenchTest()
    {
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = _logs;
        try
        {
            _switcher = new ExtronDtpCpxx(_client.Object, 8, "Bench matrix", _time);
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }

    private void Respond(params string[] replies)
    {
        foreach (var reply in replies)
            _client.Object.ResponseHandlers!.Invoke(reply);
    }

    private bool Sent(string command) => _client.Invocations.Any(invocation => Equals(invocation.Arguments[0], command));

    private async Task Connect(string[] portReplies)
    {
        typeof(CommunicationClient).GetProperty(nameof(CommunicationClient.ConnectionState))!
            .SetValue(_client.Object, ConnectionState.Connected);
        Respond(ExtronBenchCaptures.DtpCp108ConnectReplies[..2]);
        await Wait.UntilAsync(_time, ConnectStepGap, () => Sent("I"), "The model was never asked for");
        Respond(ExtronBenchCaptures.DtpCp108ConnectReplies[2..]);
        Respond(portReplies);
        Respond(ExtronBenchCaptures.DtpCp108TieReplies);
    }

    private Task Connect() => Connect(ExtronBenchCaptures.DtpCp108PortReplies);

    private void SendPoll() => typeof(ExtronDtpCpxx)
        .GetMethod("SendPoll", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(_switcher, []);

    private DeviceDetail? Detail(string label) => _switcher.Details.SingleOrDefault(detail => detail.Label == label);

    private IEnumerable<string> RejectedHdcpProbes() => _logs.Logs
        .Where(log => log.Level == LogLevel.Debug && log.Message.StartsWith("The switcher has no output for "))
        .Select(log => log.Message["The switcher has no output for ".Length..].Replace(": E13", ""));

    [Fact]
    public void Banner_ShowsTheFirmwareAndPartNumber()
    {
        Respond(ExtronBenchCaptures.DtpCp108Banner);

        Assert.Equal(new DeviceDetail("Firmware", "2.04"), Detail("Firmware"));
        Assert.Equal(new DeviceDetail("Part Number", "60-1381-01"), Detail("Part Number"));
    }

    [Fact]
    public void BannerCutShort_IsLoggedWithTheRawText()
    {
        Respond("(c) Copyright 2025, Extron Electronics, DTPCP108, V2.04");

        var warning = Assert.Single(_logs.Logs, log => log.Level == LogLevel.Warning);
        Assert.Equal("Unable to read the firmware and part number from the banner (c) Copyright 2025, Extron Electronics, DTPCP108, V2.04",
            warning.Message);
        Assert.Null(Detail("Firmware"));
    }

    [Fact]
    public async Task ConnectBurst_RejectionsGoToTheOutputHdcpQueriesThatCausedThem()
    {
        await Connect();

        Assert.Equal(HdcpProbesWithNoOutput, RejectedHdcpProbes());
        Assert.Equal(10, _switcher.Outputs.Count);
        Assert.True(_switcher.ComposedOutputs[4].Secondary.InUse);
        Assert.DoesNotContain(_logs.Logs, log => log.Level >= LogLevel.Warning);
        Assert.Empty(_switcher.GetIssues());
        Respond("E10", "E10", "E10", "E10");
        Assert.All(ReadLabels[..4], label => Assert.Equal(new DeviceDetail(label, "Not supported: E10", DetailTone.Normal), Detail(label)));
    }

    [Fact]
    public async Task TieFromAnotherController_InTheConnectBurst_UpdatesTheTieAndTakesNoCommand()
    {
        var notice = Array.IndexOf(ExtronBenchCaptures.DtpCp108PortReplies, "HdcpO4*0") + 1;
        string[] portReplies =
            [..ExtronBenchCaptures.DtpCp108PortReplies[..notice], "Out1 In2 All", ..ExtronBenchCaptures.DtpCp108PortReplies[notice..]];
        string? tieDuringTheBurst = null;
        _client.Object.ResponseHandlers += reply =>
        {
            if (reply == "Out1 In2 All")
                tieDuringTheBurst = _switcher.ComposedOutputs[0].Primary.StreamAddress;
        };

        await Connect(portReplies);

        Assert.Equal("2", tieDuringTheBurst);
        Assert.Equal(HdcpProbesWithNoOutput, RejectedHdcpProbes());
        Assert.Equal(["", "", "", "", "", "", "1", "2"], _switcher.ComposedOutputs.Select(output => output.Primary.StreamAddress));
        Respond(IdentityReplies);
        Assert.Equal(IdentityValues, ReadLabels[..4].Select(label => Detail(label)?.Value));
        Assert.DoesNotContain(_logs.Logs, log => log.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task NewReads_GoAfterTheExistingPollAndShowTheirReplies()
    {
        await Connect();
        Respond(IdentityReplies);
        _client.Invocations.Clear();

        SendPoll();
        Respond(ExtronBenchCaptures.DtpCp108PollReply, SystemStatusReply);

        Assert.Equal(["\u001b0TC\r", SystemStatusQuery], _client.Invocations.Select(invocation => invocation.Arguments[0]));
        Assert.Equal([..IdentityValues, "12.250 35.000 1976 2004"], ReadLabels.Select(label => Detail(label)?.Value));
        Assert.Equal("2.04", Detail("Firmware")?.Value);
        Assert.Empty(_switcher.GetIssues());
    }

    [Fact]
    public async Task NewReads_Rejected_ShowNotSupportedWithNoIssueAndAreNotSentAgain()
    {
        await Connect();
        Respond("E10", "E10", "E10", "E10");
        SendPoll();
        Respond(ExtronBenchCaptures.DtpCp108PollReply, "E10");
        _client.Invocations.Clear();

        SendPoll();

        Assert.All(ReadLabels, label => Assert.Equal(new DeviceDetail(label, "Not supported: E10", DetailTone.Normal), Detail(label)));
        Assert.Equal(5, _logs.Logs.Count(log => log.Level == LogLevel.Information && log.Message.StartsWith("The switcher does not support")));
        Assert.Empty(_switcher.GetIssues());
        Assert.Equal(["\u001b0TC\r"], _client.Invocations.Select(invocation => invocation.Arguments[0]));
    }

    [Fact]
    public async Task NewReads_MissedThreeTimes_RaiseMinorReadIssuesThatTheFirstReplyResolves()
    {
        await Connect();
        SendPoll();
        Respond(ExtronBenchCaptures.DtpCp108PollReply);
        for (var poll = 1; poll < 3; poll++)
        {
            _time.Advance(ReplyTimeout);
            SendPoll();
            Respond(ExtronBenchCaptures.DtpCp108PollReply);
        }

        Assert.Empty(_switcher.GetIssues());
        Assert.All(IdentityQueries, query => Assert.Equal(3, _client.Invocations.Count(invocation => Equals(invocation.Arguments[0], query))));

        _time.Advance(ReplyTimeout);
        SendPoll();

        Assert.Equal(["firmware-build-read", "mac-read", "part-number-read", "system-status-read", "unit-name-read"],
            _switcher.GetOngoingIssues().Select(issue => issue.Key).Order());
        Assert.All(_switcher.GetOngoingIssues(), issue => Assert.Equal(IssueSeverity.Minor, issue.Severity));
        Assert.Contains(_switcher.GetOngoingIssues(),
            issue => issue.Message == "The switcher did not answer S (System Status) 3 times in a row, the last was no Sts00* reply within 5 s");
        Respond(ExtronBenchCaptures.DtpCp108PollReply, SystemStatusReply);
        Assert.DoesNotContain(_switcher.GetOngoingIssues(), issue => issue.Key == "system-status-read");
    }

    [Fact]
    public async Task RejectedRoute_WhileTheSystemStatusQueryIsPending_IsReportedAndTheQueryKeepsItsReply()
    {
        await Connect();
        Respond(IdentityReplies);
        using (Trigger.Resume(RouteTrigger))
            _switcher.RouteAV(11, 1);
        SendPoll();

        Respond("E01", ExtronBenchCaptures.DtpCp108PollReply, SystemStatusReply);

        var issue = Assert.Single(_switcher.GetIssues());
        Assert.Equal(("rejected-command", IssueStatus.Momentary, "The switcher rejected 11*1! with E01"), (issue.Key, issue.Status, issue.Message));
        Assert.Equal(RouteTrigger, Assert.Single(_logs.Logs, log => log.Level == LogLevel.Warning).TriggerId);
        Assert.Equal(new DeviceDetail("System Status", "12.250 35.000 1976 2004"), Detail("System Status"));
    }

    [Fact]
    public async Task TieFromTheRoomProcessor_WhileOurRouteAwaitsItsReply_LeavesTheRejectionToOurRoute()
    {
        await Connect();
        Respond(IdentityReplies);
        using (Trigger.Resume(RouteTrigger))
            _switcher.RouteAV(11, 1);
        SendPoll();

        Respond("Out2 In5 All", "E13", ExtronBenchCaptures.DtpCp108PollReply, SystemStatusReply);

        Assert.Equal("5", _switcher.ComposedOutputs[1].Primary.StreamAddress);
        var issue = Assert.Single(_switcher.GetIssues());
        Assert.Equal(("rejected-command", IssueStatus.Momentary, "The switcher rejected 11*1! with E13"), (issue.Key, issue.Status, issue.Message));
        Assert.Equal(RouteTrigger, Assert.Single(_logs.Logs, log => log.Level == LogLevel.Warning).TriggerId);
        Assert.Equal(new DeviceDetail("System Status", "12.250 35.000 1976 2004"), Detail("System Status"));
    }
}