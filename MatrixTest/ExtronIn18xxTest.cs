using System.Collections.Concurrent;
using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Matrix.Tests;

[Collection("LoggerFactory")]
public class ExtronIn18XxTest
{
    private static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReturnTimeout = TimeSpan.FromSeconds(5);
    private readonly ExtronIn18Xx _switcher;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();

    public ExtronIn18XxTest()
    {
        _switcher = new ExtronIn18Xx(_mockClient.Object, 6, "test matrix");
    }

    private void Respond(string response) => _mockClient.Object.ResponseHandlers!.Invoke(response);

    private void SetConnection(ConnectionState state) => typeof(CommunicationClient)
        .GetProperty(nameof(CommunicationClient.ConnectionState))!.SetValue(_mockClient.Object, state);

    private Task Poll() => (Task)typeof(ExtronIn18Xx)
        .GetMethod("Poll", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(_switcher, [CancellationToken.None])!;

    private static bool Sent(Mock<CommunicationClient> client, string command) =>
        client.Invocations.Any(invocation => Equals(invocation.Arguments[0], command));

    private ConcurrentQueue<string> RecordSends(string heldCommand, ManualResetEventSlim release)
    {
        var sent = new ConcurrentQueue<string>();
        _mockClient.Setup(x => x.Send(It.IsAny<string>())).Callback<string>(command =>
        {
            sent.Enqueue(command);
            if (command == heldCommand)
                release.Wait(HoldTimeout);
        });
        return sent;
    }

    private static (ExtronIn18Xx Switcher, ScopeCapturingLoggerFactory Logs) CreateWithCapturedLogs(Mock<CommunicationClient> client)
    {
        var logs = new ScopeCapturingLoggerFactory();
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = logs;
        try
        {
            return (new ExtronIn18Xx(client.Object, 8, "Logged matrix"), logs);
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

    [Theory]
    [InlineData(1, "1*1%")]
    [InlineData(2, "2*1%")]
    [InlineData(3, "3*1%")]
    [InlineData(4, "4*1%")]
    [InlineData(5, "5*1%")]
    [InlineData(6, "6*1%")]
    public void RouteVideo_SendsTheCommand(int input, string expectedRouteCommand)
    {
        _switcher.RouteVideo(input, 0);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)] // Constructed as an in1606
    [InlineData(9)]
    public void RouteVideo_IgnoresInvalidInputNumbers(int input)
    {
        _switcher.RouteVideo(input, 3);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void RouteVideo_IgnoresOutputParameter()
    {
        string expectedRouteCommand = "3*1%";
        _switcher.RouteVideo(3, 6);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Theory]
    [InlineData(1, "1*1$")]
    [InlineData(2, "2*1$")]
    [InlineData(3, "3*1$")]
    [InlineData(4, "4*1$")]
    [InlineData(5, "5*1$")]
    [InlineData(6, "6*1$")]
    public void RouteAudio_SendsTheCommand(int input, string expectedRouteCommand)
    {
        _switcher.RouteAudio(input, 0);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void RouteAudio_IgnoresOutputParameter()
    {
        string expectedRouteCommand = "1*1$";
        _switcher.RouteAudio(1, 10);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)] // Constructed as an in1606
    [InlineData(9)]
    public void RouteAudio_IgnoresInvalidInputNumbers(int input)
    {
        _switcher.RouteAudio(input, 3);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(1, "1*1!")]
    [InlineData(2, "2*1!")]
    [InlineData(3, "3*1!")]
    [InlineData(4, "4*1!")]
    [InlineData(5, "5*1!")]
    [InlineData(6, "6*1!")]
    public void RouteAV_SendsTheCommand(int input, string expectedRouteCommand)
    {
        _switcher.RouteAV(input, 3);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)] // Constructed as an in1606
    [InlineData(9)]
    public void RouteAV_IgnoresInvalidInputNumbers(int input)
    {
        _switcher.RouteAV(input, 3);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void RouteAV_IgnoresOutputParameter()
    {
        string expectedRouteCommand = "2*1!";
        _switcher.RouteAV(2, 3);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void Routes_BeforeTheModelReply_UseTheConstructorsInputCount()
    {
        _switcher.RouteAV(6, 1);
        _switcher.RouteVideo(7, 1);
        _switcher.RouteAudio(7, 1);

        _mockClient.Verify(x => x.Send("6*1!"), Times.Once);
        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void NumberOfInputs_BeforeTheModelReply_IsTheConstructorsCount()
    {
        Assert.Equal(6, _switcher.NumberOfInputs);
    }

    [Fact]
    public void Routes_AfterAModelReplyWithMoreInputs_UseTheDiscoveredCount()
    {
        Respond("Inf01*IN1808\r\n");
        _mockClient.Invocations.Clear();

        _switcher.RouteAV(8, 1);
        _switcher.RouteVideo(8, 1);
        _switcher.RouteAudio(8, 1);

        _mockClient.Verify(x => x.Send("8*1!"), Times.Once);
        _mockClient.Verify(x => x.Send("8*1%"), Times.Once);
        _mockClient.Verify(x => x.Send("8*1$"), Times.Once);
    }

    [Fact]
    public void Routes_AfterAModelReplyWithFewerInputs_UseTheDiscoveredCountAndWarnOnce()
    {
        var client = TestFactory.CreateCommunicationClient();
        var (switcher, logs) = CreateWithCapturedLogs(client);
        client.Object.ResponseHandlers!.Invoke("Inf01*IN1806\r\n");
        client.Invocations.Clear();

        switcher.RouteAV(6, 1);
        switcher.RouteAV(7, 1);
        switcher.RouteVideo(8, 1);
        switcher.RouteAudio(7, 1);

        client.Verify(x => x.Send("6*1!"), Times.Once);
        client.Verify(x => x.Send(It.IsAny<string>()), Times.Once);
        Assert.Single(logs.Logs, log => log.Message.Contains("reports 6 inputs") && log.Message.Contains("constructed with 8"));
        Assert.Contains(switcher.Events, e => e.Type == EventType.Error && e.Info.EndsWith("must be between 1 and 6"));
    }

    [Fact]
    public void ModelReply_MatchingTheConstructorsInputCount_DoesNotWarn()
    {
        var client = TestFactory.CreateCommunicationClient();
        var (_, logs) = CreateWithCapturedLogs(client);

        client.Object.ResponseHandlers!.Invoke("Inf01*IN1808\r\n");

        Assert.DoesNotContain(logs.Logs, log => log.Message.Contains("constructed with"));
    }

    [Fact]
    public void SetSyncTimeout_SendsTheCommand()
    {
        string expectedCommand = "\u001bT1*0SSAV\r";
        _switcher.SetSyncTimeout(0);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void SetSyncTimeout_IgnoresInvalidTimeouts()
    {
        _switcher.SetSyncTimeout(502);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void SetSyncTimeout_SetsToNeverDropSync()
    {
        string expectedCommand = "\u001bT1*501SSAV\r";
        _switcher.SetSyncTimeout(501);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Theory]
    [InlineData("IN1808\r", 8)]
    [InlineData("IN1806\r", 6)]
    [InlineData("Inf01*IN1808\r\n", 8)]
    [InlineData("Inf01*IN1808 IPCP SA\r\n", 8)]
    [InlineData("Inf01*IN1808 IPCP MA 70\r\n", 8)]
    [InlineData("Inf01*IN1806\r\n", 6)]
    [InlineData("Inf01*IN1804\r\n", 4)]
    public void ResponseHandler_HandlesModelNumber(string response, int expectedInputs)
    {
        Respond(response);

        Assert.Equal(expectedInputs, _switcher.Inputs.Count);
        Assert.Equal(Enumerable.Range(1, expectedInputs), _switcher.Inputs.Select(input => input.Number));
        Assert.Equal($"Input {expectedInputs}", _switcher.Inputs[^1].Name);
    }

    [Fact]
    public void Outputs_ExistFromConstructionAsTheHdmiAndTwistedPairConnectors()
    {
        var outputs = _switcher.GetOutputs();

        Assert.Equal(2, outputs.Count);
        Assert.Same(_switcher.ComposedOutputs[0].Primary, outputs[0]);
        Assert.Same(_switcher.ComposedOutputs[0].Secondary, outputs[1]);
        Assert.Equal(["Output 1A", "Output 1B"], outputs.Select(output => output.Name));
        Assert.All(outputs, output => Assert.Equal(AVEndpointType.Decoder, output.DeviceType));
        Assert.Equal(1, _switcher.NumberOfOutputs);
    }

    [Fact]
    public void ModelResponse_NotifiesSubscribersOnceThePortsExist()
    {
        var seen = new List<(int Inputs, int Outputs)>();
        _switcher.EndpointsChangedHandlers += () => seen.Add((_switcher.GetInputs().Count, _switcher.GetOutputs().Count));

        Respond("Inf01*IN1808\r\n");

        Assert.Equal([(8, 2)], seen);
    }

    [Fact]
    public void ModelChange_KeepsTheExistingInputsAndNotifiesSubscribers()
    {
        Respond("Inf01*IN1806\r\n");
        var first = _switcher.GetInputs()[0];
        var seen = new List<int>();
        _switcher.EndpointsChangedHandlers += () => seen.Add(_switcher.GetInputs().Count);

        Respond("Inf01*IN1808\r\n");

        Assert.Equal([8], seen);
        Assert.Same(first, _switcher.GetInputs()[0]);
        Assert.Equal(8, _switcher.Inputs[^1].Number);
    }

    [Fact]
    public void Reconnect_KeepsThePortObjectsWhenTheCountIsUnchanged()
    {
        Respond("Inf01*IN1808\r\n");
        var inputs = _switcher.GetInputs();
        var outputs = _switcher.GetOutputs();
        var changes = 0;
        _switcher.EndpointsChangedHandlers += () => changes++;

        SetConnection(ConnectionState.Connected);
        SetConnection(ConnectionState.Disconnected);
        SetConnection(ConnectionState.Connected);
        Respond("Inf01*IN1808\r\n");

        Assert.Equal(0, changes);
        Assert.Equal(8, _switcher.GetInputs().Count);
        Assert.All(Enumerable.Range(0, 8), index => Assert.Same(inputs[index], _switcher.GetInputs()[index]));
        Assert.All(Enumerable.Range(0, 2), index => Assert.Same(outputs[index], _switcher.GetOutputs()[index]));
    }

    [Fact]
    public async Task Connect_EnablesVerboseModeAndAsksForTheModel()
    {
        SetConnection(ConnectionState.Connected);

        await Wait.UntilAsync(() => Sent(_mockClient, "1I"), "The model was never asked for");
        _mockClient.Verify(client => client.Send("\u001b3CV\r"), Times.Once);
        _mockClient.Verify(client => client.Send("1I"), Times.Once);
    }

    [Fact]
    public async Task Constructor_InitialisesAnAlreadyConnectedClient()
    {
        var client = TestFactory.CreateCommunicationClient();
        typeof(CommunicationClient).GetProperty(nameof(CommunicationClient.ConnectionState))!
            .SetValue(client.Object, ConnectionState.Connected);

        _ = new ExtronIn18Xx(client.Object, 8, "Already connected");

        await Wait.UntilAsync(() => Sent(client, "1I"), "The model was never asked for");
        client.Verify(c => c.Send("\u001b3CV\r"), Times.Once);
        client.Verify(c => c.Send("1I"), Times.Once);
    }

    [Fact]
    public async Task Connect_DoesNotBlockTheClientWhileTheSpacedSendsRun()
    {
        using var release = new ManualResetEventSlim();
        var sent = RecordSends("\u001b3CV\r", release);

        var connect = Task.Run(() => SetConnection(ConnectionState.Connected));

        await connect.WaitAsync(ReturnTimeout);
        release.Set();
        await Wait.UntilAsync(() => sent.Count == 2, "The model was never asked for");
        Assert.Equal(["\u001b3CV\r", "1I"], sent);
    }

    [Fact]
    public async Task Constructor_DoesNotBlockOnAnAlreadyConnectedClient()
    {
        var client = TestFactory.CreateCommunicationClient();
        typeof(CommunicationClient).GetProperty(nameof(CommunicationClient.ConnectionState))!
            .SetValue(client.Object, ConnectionState.Connected);
        using var release = new ManualResetEventSlim();
        client.Setup(x => x.Send("\u001b3CV\r")).Callback(() => release.Wait(HoldTimeout));

        var construct = Task.Run(() => new ExtronIn18Xx(client.Object, 8, "Already connected"));

        await construct.WaitAsync(ReturnTimeout);
        release.Set();
        await Wait.UntilAsync(() => Sent(client, "1I"), "The model was never asked for");
    }

    [Fact]
    public void ModelResponse_QueriesSignalAndHdcpForEveryPort()
    {
        Respond("Inf01*IN1808\r\n");

        foreach (var command in new[]
                 {
                     "\u001b0LS\r", "\u001bI1HDCP\r", "\u001bI2HDCP\r", "\u001bI3HDCP\r", "\u001bI4HDCP\r",
                     "\u001bI5HDCP\r", "\u001bI6HDCP\r", "\u001bI7HDCP\r", "\u001bI8HDCP\r",
                     "\u001bO1HDCP\r", "\u001bO2HDCP\r"
                 })
            _mockClient.Verify(client => client.Send(command), Times.Once);
        _mockClient.Verify(client => client.Send("\u001bI9HDCP\r"), Times.Never);
    }

    [Fact]
    public async Task Heartbeat_RefreshesStatusOnlyWhileConnected()
    {
        await Poll();
        _mockClient.Verify(client => client.Send(It.IsAny<string>()), Times.Never);

        SetConnection(ConnectionState.Connected);
        await Wait.UntilAsync(() => Sent(_mockClient, "1I"), "The model was never asked for");
        _mockClient.Invocations.Clear();
        await Poll();
        _mockClient.Verify(client => client.Send("1I"), Times.Once);
        _mockClient.Verify(client => client.Send("\u001b0LS\r"), Times.Once);

        Respond("Inf01*IN1808\r\n");
        _mockClient.Invocations.Clear();
        await Poll();
        _mockClient.Verify(client => client.Send("1I"), Times.Never);
        _mockClient.Verify(client => client.Send("\u001b0LS\r"), Times.Once);
        _mockClient.Verify(client => client.Send("\u001bI8HDCP\r"), Times.Once);
        _mockClient.Verify(client => client.Send("\u001bO2HDCP\r"), Times.Once);

        SetConnection(ConnectionState.Disconnected);
        _mockClient.Invocations.Clear();
        await Poll();
        _mockClient.Verify(client => client.Send(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void SignalPresence_SetsEveryInputAndNotifiesSubscribers()
    {
        Respond("Inf01*IN1808\r\n");
        var inputs = _switcher.GetInputs();
        var changes = 0;
        inputs[6].InputStatusChangedHandlers += (_, _, _) => changes++;

        Respond("IN00 1*0*0*0*0*0*1*0\r\n");

        Assert.Equal(ConnectionState.Connected, inputs[0].InputConnectionStatus);
        Assert.Equal(ConnectionState.Disconnected, inputs[1].InputConnectionStatus);
        Assert.Equal(ConnectionState.Connected, inputs[6].InputConnectionStatus);
        Assert.Equal(ConnectionState.Disconnected, inputs[7].InputConnectionStatus);

        Respond("IN00 1*0*0*0*0*0*0*0\r\n");

        Assert.Equal(ConnectionState.Disconnected, inputs[6].InputConnectionStatus);
        Assert.Equal(2, changes);
    }

    [Theory]
    [InlineData("IN00 1*0*1*0*0*0*0*0\r\n")]
    [InlineData("In00 1*0*1*0*0*0*0*0\r\n")]
    public void SignalPresence_MatchesTheHeaderInEitherCase(string response)
    {
        Respond("Inf01*IN1808\r\n");

        Respond(response);

        Assert.Equal(
            [ConnectionState.Connected, ConnectionState.Disconnected, ConnectionState.Connected, ConnectionState.Disconnected,
             ConnectionState.Disconnected, ConnectionState.Disconnected, ConnectionState.Disconnected, ConnectionState.Disconnected],
            _switcher.GetInputs().Select(input => input.InputConnectionStatus));
    }

    [Theory]
    [InlineData("IN00 1*1*1")]
    [InlineData("IN00 1*1*1*1*1*1*1*1*1")]
    public void SignalPresence_LogsRepliesThatDoNotCoverEveryInput(string response)
    {
        var client = TestFactory.CreateCommunicationClient();
        var (switcher, logs) = CreateWithCapturedLogs(client);
        client.Object.ResponseHandlers!.Invoke("Inf01*IN1808\r\n");

        client.Object.ResponseHandlers!.Invoke(response);

        Assert.Contains(logs.Logs, log => log.Message.Contains(response[4..]) && log.Message.Contains("8 inputs"));
        Assert.All(switcher.GetInputs(), input => Assert.Equal(ConnectionState.Unknown, input.InputConnectionStatus));
    }

    [Theory]
    [InlineData("IN00 1*1*1")]
    [InlineData("IN00 1*1*1*1*1*1*1*1*1")]
    [InlineData("1*1*1*1*1*1*1*1")]
    public void SignalPresence_IgnoresRepliesThatDoNotCoverEveryInput(string response)
    {
        Respond("Inf01*IN1808\r\n");

        Respond(response);

        Assert.Equal(8, _switcher.NumberOfInputs);
        Assert.All(_switcher.GetInputs(), input => Assert.Equal(ConnectionState.Unknown, input.InputConnectionStatus));
    }

    [Fact]
    public void InputHdcp_SetsHdcpWithoutChangingSignalState()
    {
        Respond("Inf01*IN1808\r\nIN00 0*1*0*0*0*0*1*0\r\n");
        var inputs = _switcher.GetInputs();

        Respond("HdcpI02*2\r\nHdcpI 07*1\r\n");

        Assert.Equal(HdcpStatus.Active, inputs[1].InputHdcpStatus);
        Assert.Equal(HdcpStatus.NotSupported, inputs[6].InputHdcpStatus);

        Respond("HdcpI02*0\r\n");

        Assert.Equal(HdcpStatus.Unknown, inputs[1].InputHdcpStatus);
        Assert.Equal(ConnectionState.Connected, inputs[1].InputConnectionStatus);
        Assert.Equal(ConnectionState.Disconnected, inputs[0].InputConnectionStatus);
    }

    [Theory]
    [InlineData("HdcpI00*2")]
    [InlineData("HdcpI09*2")]
    [InlineData("HdcpI02")]
    public void InputHdcp_LogsPortsOutsideTheInputs(string response)
    {
        var client = TestFactory.CreateCommunicationClient();
        var (_, logs) = CreateWithCapturedLogs(client);
        client.Object.ResponseHandlers!.Invoke("Inf01*IN1808\r\n");

        client.Object.ResponseHandlers!.Invoke(response);

        Assert.Contains(logs.Logs, log => log.Message.Contains($"HDCP status {response[5..]} "));
    }

    [Theory]
    [InlineData("HdcpI00*2")]
    [InlineData("HdcpI09*2")]
    [InlineData("HdcpI02")]
    public void InputHdcp_IgnoresPortsOutsideTheInputs(string response)
    {
        Respond("Inf01*IN1808\r\n");
        Assert.Equal(8, _switcher.GetInputs().Count);

        Respond(response);

        Assert.All(_switcher.GetInputs(), input => Assert.Equal(HdcpStatus.Unknown, input.InputHdcpStatus));
    }

    [Fact]
    public void OutputHdcp_ReportsSinkAndHdcpPerConnector()
    {
        var outputs = _switcher.GetOutputs();
        var changes = 0;
        outputs[0].OutputStatusChangedHandlers += (_, _, _) => changes++;

        Respond("HdcpO1*2\r\nHdcpO2*1\r\n");

        Assert.Equal(ConnectionState.Connected, outputs[0].OutputConnectionStatus);
        Assert.Equal(HdcpStatus.Available, outputs[0].OutputHdcpStatus);
        Assert.Equal(ConnectionState.Connected, outputs[1].OutputConnectionStatus);
        Assert.Equal(HdcpStatus.NotSupported, outputs[1].OutputHdcpStatus);

        Respond("HdcpO 01*0\r\n");

        Assert.Equal(ConnectionState.Disconnected, outputs[0].OutputConnectionStatus);
        Assert.Equal(HdcpStatus.Unknown, outputs[0].OutputHdcpStatus);
        Assert.Equal(4, changes);
    }

    [Theory]
    [InlineData("HplgO1*1", 0, ConnectionState.Connected)]
    [InlineData("HplgO2*1", 1, ConnectionState.Connected)]
    [InlineData("HplgO1*0", 0, ConnectionState.Disconnected)]
    [InlineData("HplgO2*2", 1, ConnectionState.Disconnected)]
    public void Hotplug_SetsTheSinkState(string response, int index, ConnectionState expected)
    {
        Respond(response);

        Assert.Equal(expected, _switcher.GetOutputs()[index].OutputConnectionStatus);
    }

    [Fact]
    public void LoopOut_IsIgnored()
    {
        Respond("HdcpO1*2\r\nHdcpO3*2\r\nHplgO3*1\r\n");

        var outputs = _switcher.GetOutputs();
        Assert.Equal(ConnectionState.Connected, outputs[0].OutputConnectionStatus);
        Assert.Equal(HdcpStatus.Available, outputs[0].OutputHdcpStatus);
        Assert.Equal(ConnectionState.Unknown, outputs[1].OutputConnectionStatus);
        Assert.Equal(HdcpStatus.Unknown, outputs[1].OutputHdcpStatus);
    }

    [Fact]
    public void MultiLineChunk_HandlesEveryLine()
    {
        Respond("Inf01*IN1808\r\nIN00 1*0*0*0*0*0*0*1\r\nHdcpI01*2\r\nHdcpO1*1\r\n");

        var inputs = _switcher.GetInputs();
        Assert.Equal(8, inputs.Count);
        Assert.Equal(ConnectionState.Connected, inputs[0].InputConnectionStatus);
        Assert.Equal(ConnectionState.Connected, inputs[7].InputConnectionStatus);
        Assert.Equal(HdcpStatus.Active, inputs[0].InputHdcpStatus);
        Assert.Equal(ConnectionState.Connected, _switcher.GetOutputs()[0].OutputConnectionStatus);
    }

    [Fact]
    public void Disconnect_ClearsStaleStatusWithoutReplacingPorts()
    {
        SetConnection(ConnectionState.Connected);
        Respond("Inf01*IN1808\r\nIN00 1*1*1*1*1*1*1*1\r\nHdcpI01*2\r\nHdcpO1*2\r\n");
        var input = _switcher.GetInputs()[0];
        var output = _switcher.GetOutputs()[0];

        SetConnection(ConnectionState.Disconnected);

        Assert.Same(input, _switcher.GetInputs()[0]);
        Assert.Same(output, _switcher.GetOutputs()[0]);
        Assert.Equal(ConnectionState.Unknown, input.InputConnectionStatus);
        Assert.Equal(HdcpStatus.Unknown, input.InputHdcpStatus);
        Assert.Equal(ConnectionState.Unknown, output.OutputConnectionStatus);
        Assert.Equal(HdcpStatus.Unknown, output.OutputHdcpStatus);
    }

    [Theory]
    [InlineData("VnamI3*BluRay\r", 2, "BluRay")]
    [InlineData("VnamI1*Doc Cam\r", 0, "Doc Cam")]
    [InlineData("VnamI7*Left Laptop\r", 6, "Left Laptop")]
    [InlineData("VnamI7*\r", 6, "")]
    public void HandleResponse_UpdatesInputName(string response, int index, string expectedName)
    {
        _mockClient.Object.ResponseHandlers!.Invoke("IN1808\r");
        _mockClient.Object.ResponseHandlers!.Invoke(response);

        Assert.Equal(expectedName, _switcher.Inputs[index].Name);
    }

    [Fact]
    public void HandleResponse_DoesNotRenameAnInputFromAnOutputName()
    {
        Respond("IN1808\r");

        Respond("VnamO1*Projector\r");

        Assert.Equal("Input 1", _switcher.Inputs[0].Name);
    }

    [Fact]
    public void HandleResponse_KeepsAnInputNameThatContainsAnAsterisk()
    {
        Respond("IN1808\r");

        Respond("VnamI3*Cam*1\r");

        Assert.Equal("Cam*1", _switcher.Inputs[2].Name);
    }

    [Fact]
    public void SetSyncTimeout_StatesTheLimitItEnforces()
    {
        _switcher.SetSyncTimeout(502);

        Assert.Contains(_switcher.Events,
            e => e.Type == EventType.Error && e.Info == "The sync timeout can't be longer than 501 seconds");
    }

    [Fact]
    public async Task SetVideoMute_ReturnsBeforeTheSpacedSends()
    {
        using var release = new ManualResetEventSlim();
        var sent = RecordSends("1*2B", release);

        var mute = Task.Run(() => _switcher.SetVideoMute(MuteState.On));

        await mute.WaitAsync(ReturnTimeout);
        release.Set();
        await Wait.UntilAsync(() => sent.Count == 3, "The video mute never reached output 3");
        Assert.Equal(["1*2B", "2*2B", "3*2B"], sent);
    }

    [Fact]
    public async Task SetVideoMute_SendsSuccessiveCallsInTheOrderTheyWereMade()
    {
        using var release = new ManualResetEventSlim(true);
        var sent = RecordSends("", release);

        _switcher.SetVideoMute(MuteState.On);
        _switcher.SetVideoMute(MuteState.Off);

        await Wait.UntilAsync(() => sent.Count == 6, "The video mutes never reached output 3");
        Assert.Equal(["1*2B", "2*2B", "3*2B", "1*0B", "2*0B", "3*0B"], sent);
    }

    [Fact]
    public async Task SetVideoMute_SendsUnderTheCallersTriggerId()
    {
        var ids = new ConcurrentQueue<string?>();
        _mockClient.Setup(x => x.Send(It.IsAny<string>())).Callback(() => ids.Enqueue(Trigger.Id));
        var id = Trigger.Start();

        _switcher.SetVideoMute(MuteState.On);

        await Wait.UntilAsync(() => ids.Count == 3, "The video mute never reached output 3");
        Assert.All(ids, sentUnder => Assert.Equal(id, sentUnder));
    }

    [Fact]
    public async Task SetVideoMute_KeepsSendingAfterAnEarlierSequenceFails()
    {
        using var release = new ManualResetEventSlim(true);
        var sent = RecordSends("", release);
        var changes = 0;
        _switcher.CommunicationStateHandlers += _ =>
        {
            if (Interlocked.Increment(ref changes) <= 2)
                throw new InvalidOperationException("A subscriber failed");
        };

        _switcher.SetVideoMute(MuteState.On);
        _switcher.SetVideoMute(MuteState.Off);

        await Wait.UntilAsync(() => sent.Contains("3*0B"), "The second video mute never reached output 3");
        Assert.Equal(["1*2B", "1*0B", "2*0B", "3*0B"], sent);
        Assert.Contains(_switcher.Errors, error => error.Message == "The video mute sends failed");
    }
}
