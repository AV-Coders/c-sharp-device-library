using System.Collections.Concurrent;
using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AVCoders.Matrix.Tests;

public class ExtronIn18XxTest
{
    private static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReturnTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectStepGap = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan VideoMuteStepGap = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan QueryReplyTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReconcileGap = TimeSpan.FromMilliseconds(300);
    private static readonly string[] IdentityQueries = ["*Q", "N", "\u001bCN\r", "\u001bCH\r"];
    private const string TemperatureQuery = "\u001b20STAT\r";
    private static readonly string[] SettingsQueries =
    [
        "\u001bM1SSAV\r", "\u001bT1SSAV\r", "\u001bN1HDCP\r", "\u001bAUSW\r", "1*B", "2*B", "3*B",
        "\u001bS1HDCP\r", "\u001bS2HDCP\r", "\u001bS3HDCP\r"
    ];
    internal static readonly string[] SettingsReplies =
        ["SsavM1*1", "SsavT1*501", "HdcpN1*1", "Ausw0", "Vmt1*0", "Vmt2*0", "Vmt3*0", "HdcpS1*1", "HdcpS2*1", "HdcpS3*1"];
    private readonly FakeTimeProvider _time = new();
    private readonly ExtronIn18Xx _switcher;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();

    public ExtronIn18XxTest()
    {
        _switcher = new ExtronIn18Xx(_mockClient.Object, 6, "test matrix", _time);
    }

    private void Respond(string response) => _mockClient.Object.ResponseHandlers!.Invoke(response);

    private void SetConnection(ConnectionState state) => typeof(CommunicationClient)
        .GetProperty(nameof(CommunicationClient.ConnectionState))!.SetValue(_mockClient.Object, state);

    private Task Poll() => (Task)typeof(ExtronIn18Xx)
        .GetMethod("Poll", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(_switcher, [CancellationToken.None])!;

    private static bool Sent(Mock<CommunicationClient> client, string command) =>
        client.Invocations.Any(invocation => Equals(invocation.Arguments[0], command));

    private DeviceDetail? Detail(string label) => _switcher.Details.SingleOrDefault(detail => detail.Label == label);

    private void RespondToTheSettings()
    {
        foreach (var reply in SettingsReplies)
            Respond(reply);
    }

    private async Task ConnectAsTheBenchIn1808()
    {
        SetConnection(ConnectionState.Connected);
        await Wait.UntilAsync(_time, ConnectStepGap, () => Sent(_mockClient, "1I"), "The model was never asked for");
        foreach (var reply in ExtronBenchCaptures.In1808ConnectReplies)
            Respond(reply);
        foreach (var reply in ExtronBenchCaptures.In1808StatusReplies)
            Respond(reply);
        foreach (var reply in ExtronBenchCaptures.In1808IdentityReplies)
            Respond(reply);
        RespondToTheSettings();
    }

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

    private List<string> RecordSends()
    {
        var sent = new List<string>();
        _mockClient.Setup(x => x.Send(It.IsAny<string>())).Callback<string>(sent.Add);
        return sent;
    }

    [Fact]
    public void FailedSend_ReportsCommunicationHasFailed()
    {
        _mockClient.Setup(client => client.Send(It.IsAny<string>())).Throws(new IOException("Oh No!"));

        _switcher.RouteAV(1, 1);

        Assert.Equal(CommunicationState.Error, _switcher.CommunicationState);
    }

    [Theory]
    [InlineData(1, "1*1%")]
    [InlineData(6, "6*1%")]
    public void RouteVideo_SendsTheCommandWhateverTheOutput(int input, string expectedRouteCommand)
    {
        _switcher.RouteVideo(input, 6);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(9)]
    public void RouteVideo_IgnoresInvalidInputNumbers(int input)
    {
        _switcher.RouteVideo(input, 3);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(1, "1*1$")]
    [InlineData(6, "6*1$")]
    public void RouteAudio_SendsTheCommandWhateverTheOutput(int input, string expectedRouteCommand)
    {
        _switcher.RouteAudio(input, 10);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(9)]
    public void RouteAudio_IgnoresInvalidInputNumbers(int input)
    {
        _switcher.RouteAudio(input, 3);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(1, "1*1!")]
    [InlineData(6, "6*1!")]
    public void RouteAV_SendsTheCommandWhateverTheOutputAndReportsCommunicationIsOkay(int input, string expectedRouteCommand)
    {
        _switcher.RouteAV(input, 3);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
        Assert.Equal(CommunicationState.Okay, _switcher.CommunicationState);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(9)]
    public void RouteAV_IgnoresInvalidInputNumbers(int input)
    {
        _switcher.RouteAV(input, 3);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
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
    public void SetSyncTimeout_SendsTheCommand()
    {
        string expectedCommand = "\u001bT1*0SSAV\r";
        _switcher.SetSyncTimeout(0);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void SetSyncTimeout_RefusesALongerTimeoutAndStatesTheLimit()
    {
        _switcher.SetSyncTimeout(502);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
        Assert.Contains(_switcher.Events,
            e => e.Type == EventType.Error && e.Info == "The sync timeout can't be longer than 501 seconds");
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
    public async Task Connect_EnablesVerboseModeAndAsksForTheModelWithoutBlockingTheClient()
    {
        using var release = new ManualResetEventSlim();
        var sent = RecordSends("\u001b3CV\r", release);

        var connect = Task.Run(() => SetConnection(ConnectionState.Connected));

        await connect.WaitAsync(ReturnTimeout);
        release.Set();
        await Wait.UntilAsync(_time, ConnectStepGap, () => sent.Count == 2, "The model was never asked for");
        Assert.Equal(["\u001b3CV\r", "1I"], sent);
    }

    [Fact]
    public async Task Constructor_InitialisesAnAlreadyConnectedClientWithoutBlocking()
    {
        var client = TestFactory.CreateCommunicationClient();
        typeof(CommunicationClient).GetProperty(nameof(CommunicationClient.ConnectionState))!
            .SetValue(client.Object, ConnectionState.Connected);
        using var release = new ManualResetEventSlim();
        client.Setup(x => x.Send("\u001b3CV\r")).Callback(() => release.Wait(HoldTimeout));

        var construct = Task.Run(() => new ExtronIn18Xx(client.Object, 8, "Already connected", _time));

        await construct.WaitAsync(ReturnTimeout);
        release.Set();
        await Wait.UntilAsync(_time, ConnectStepGap, () => Sent(client, "1I"), "The model was never asked for");
        client.Verify(c => c.Send("\u001b3CV\r"), Times.Once);
        client.Verify(c => c.Send("1I"), Times.Once);
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
        await Wait.UntilAsync(_time, ConnectStepGap, () => Sent(_mockClient, "1I"), "The model was never asked for");
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

    [Fact]
    public void SignalPresence_WithoutTheHeader_IsIgnored()
    {
        Respond("Inf01*IN1808\r\n");

        Respond("1*1*1*1*1*1*1*1");

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
    public async Task SetVideoMute_ReturnsBeforeTheSpacedSends()
    {
        using var release = new ManualResetEventSlim();
        var sent = RecordSends("1*2B", release);

        var mute = Task.Run(() => _switcher.SetVideoMute(MuteState.On));

        await mute.WaitAsync(ReturnTimeout);
        release.Set();
        await Wait.UntilAsync(_time, VideoMuteStepGap, () => sent.Count == 3, "The video mute never reached output 3");
        Assert.Equal(["1*2B", "2*2B", "3*2B"], sent);
    }

    [Fact]
    public async Task SetVideoMute_SendsSuccessiveCallsInTheOrderTheyWereMade()
    {
        using var release = new ManualResetEventSlim(true);
        var sent = RecordSends("", release);

        _switcher.SetVideoMute(MuteState.On);
        _switcher.SetVideoMute(MuteState.Off);

        await Wait.UntilAsync(_time, VideoMuteStepGap, () => sent.Count == 6, "The video mutes never reached output 3");
        Assert.Equal(["1*2B", "2*2B", "3*2B", "1*0B", "2*0B", "3*0B"], sent);
    }

    [Fact]
    public async Task SetVideoMute_SendsUnderTheCallersTriggerId()
    {
        var ids = new ConcurrentQueue<string?>();
        _mockClient.Setup(x => x.Send(It.IsAny<string>())).Callback(() => ids.Enqueue(Trigger.Id));
        var id = Trigger.Start();

        _switcher.SetVideoMute(MuteState.On);

        await Wait.UntilAsync(_time, VideoMuteStepGap, () => ids.Count == 3, "The video mute never reached output 3");
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

        await Wait.UntilAsync(_time, VideoMuteStepGap, () => sent.Contains("3*0B"), "The second video mute never reached output 3");
        Assert.Equal(["1*2B", "1*0B", "2*0B", "3*0B"], sent);
        Assert.Contains(_switcher.Errors, error => error.Message == "The video mute sends failed");
    }

    [Theory]
    [InlineData("Inf01*IN1808 IPCP SA\r\n", "IN1808 IPCP SA")]
    [InlineData("IN1806\r", "IN1806")]
    public void ModelResponse_ShowsTheModel(string response, string expectedModel)
    {
        Respond(response);

        Assert.Equal(new DeviceDetail("Model", expectedModel), Detail("Model"));
    }

    [Fact]
    public void ModelResponse_QueriesTheStatusBeforeReadingTheIdentity()
    {
        var sent = RecordSends();

        Respond("Inf01*IN1808\r\n");

        Assert.Equal("\u001b0LS\r", sent[0]);
        Assert.Equal(["\u001bO2HDCP\r", ..IdentityQueries, ..SettingsQueries], sent.SkipWhile(command => command != "\u001bO2HDCP\r"));
    }

    [Fact]
    public void IdentityReplies_AreShownAsReceived()
    {
        Respond("Inf01*IN1808\r\n");

        Respond("Bld1.00.0012\r\nPno60-1615-01\r\nIpn IN1808-12-34-56\r\nIph 00-05-A6-12-34-56\r\n");

        Assert.Equal("1.00.0012", Detail("Firmware")?.Value);
        Assert.Equal("60-1615-01", Detail("Part Number")?.Value);
        Assert.Equal("IN1808-12-34-56", Detail("Unit Name")?.Value);
        Assert.Equal("00-05-A6-12-34-56", Detail("MAC")?.Value);
        Assert.Empty(_switcher.GetOngoingIssues());
    }

    [Fact]
    public void RejectedIdentityQuery_ShowsItIsNotSupportedWithNoIssue()
    {
        Respond("Inf01*IN1808\r\n");
        foreach (var reply in ExtronBenchCaptures.In1808StatusReplies)
            Respond(reply);
        var communicationState = _switcher.CommunicationState;

        Respond("E10\r\n");

        Assert.Equal(new DeviceDetail("Firmware", "Not supported: E10", DetailTone.Normal), Detail("Firmware"));
        Assert.Empty(_switcher.GetIssues());
        Assert.Equal(communicationState, _switcher.CommunicationState);
    }

    [Fact]
    public void UnknownTaggedModel_IsShownAndRaisesAnIssue()
    {
        Respond("Inf01*IN1609\r\n");

        Assert.Equal("IN1609", Detail("Model")?.Value);
        var issue = Assert.Single(_switcher.GetOngoingIssues());
        Assert.Equal("model", issue.Key);
        Assert.Equal(IssueSeverity.Major, issue.Severity);
        Assert.Contains("Inf01*IN1609", issue.Message);
    }

    [Fact]
    public void RejectionWithNoOutstandingQuery_RaisesNoIssue()
    {
        Respond("E13\r\n");

        Assert.Empty(_switcher.GetIssues());
        Assert.Empty(_switcher.Details);
    }

    [Fact]
    public async Task Heartbeat_ReadsTheTemperatureAfterTheStatusAndNotTheIdentity()
    {
        SetConnection(ConnectionState.Connected);
        Respond("Inf01*IN1808\r\n");
        var sent = RecordSends();

        await Poll();

        Assert.Equal("\u001b0LS\r", sent[0]);
        Assert.Equal(TemperatureQuery, sent[^1]);
        Assert.DoesNotContain(sent, IdentityQueries.Contains);
    }

    [Fact]
    public void DefaultUnitNameReply_IsNotTakenAsTheModel()
    {
        Respond("Inf01*IN1808\r\n");
        var sent = RecordSends();

        Respond("IN1808-12-34-56\r\n");

        Assert.Empty(sent);
        Assert.Equal("IN1808", Detail("Model")?.Value);
    }

    [Fact]
    public async Task UnansweredIdentityQuery_IsSentAgainAtTheNextPoll()
    {
        SetConnection(ConnectionState.Connected);
        Respond("Inf01*IN1808\r\n");
        Respond("Bld1.00.0012\r\nPno60-1615-01\r\nIph 00-05-A6-12-34-56\r\n");
        _mockClient.Invocations.Clear();

        _time.Advance(QueryReplyTimeout);
        await Poll();

        _mockClient.Verify(client => client.Send("\u001bCN\r"), Times.Once);
        foreach (var answered in new[] { "*Q", "N", "\u001bCH\r" })
            _mockClient.Verify(client => client.Send(answered), Times.Never);
    }

    [Fact]
    public async Task RejectedTemperatureQuery_IsNotSentAgainUntilTheSwitcherReconnects()
    {
        SetConnection(ConnectionState.Connected);
        await Poll();

        Respond("Inf01*IN1808\r\nIn00 0*1*0*1*0*0*0*0\r\nHdcpO1*0\r\nHdcpO2*0\r\n");
        RespondToTheSettings();
        Respond("E10\r\n");

        Assert.Equal(new DeviceDetail("Temperature", "Not supported: E10", DetailTone.Normal), Detail("Temperature"));
        Assert.Empty(_switcher.GetIssues());
        _mockClient.Invocations.Clear();
        await Poll();
        _mockClient.Verify(client => client.Send(TemperatureQuery), Times.Never);

        SetConnection(ConnectionState.Disconnected);
        SetConnection(ConnectionState.Connected);
        _mockClient.Invocations.Clear();
        await Poll();
        _mockClient.Verify(client => client.Send(TemperatureQuery), Times.Once);
    }

    [Fact]
    public async Task BusyTemperatureQuery_IsSentAgainAndRaisesTheReadIssueOnTheThirdInARow()
    {
        await ConnectAsTheBenchIn1808();
        _mockClient.Invocations.Clear();
        await Poll();
        foreach (var reply in ExtronBenchCaptures.In1808StatusReplies)
            Respond(reply);
        RespondToTheSettings();
        Respond("E22\r\n");
        await Poll();
        foreach (var reply in ExtronBenchCaptures.In1808StatusReplies)
            Respond(reply);
        RespondToTheSettings();
        Respond("E22\r\n");

        Assert.Empty(_switcher.GetIssues());
        _mockClient.Verify(client => client.Send(TemperatureQuery), Times.Exactly(2));

        await Poll();
        foreach (var reply in ExtronBenchCaptures.In1808StatusReplies)
            Respond(reply);
        RespondToTheSettings();
        Respond("E22\r\n");

        var issue = Assert.Single(_switcher.GetOngoingIssues());
        Assert.Equal("temperature-read", issue.Key);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Equal("The switcher did not answer Esc 20STAT (Temperature) 3 times in a row, the last was E22", issue.Message);
        Respond("20Stat 36\r\n");
        Assert.Empty(_switcher.GetOngoingIssues());
    }

    [Fact]
    public async Task UnansweredTemperatureQuery_IsSentAgainAndRaisesTheReadIssueOnTheThirdMiss()
    {
        SetConnection(ConnectionState.Connected);
        _mockClient.Invocations.Clear();
        await Poll();
        RespondToTheSettings();
        _time.Advance(QueryReplyTimeout);
        await Poll();
        RespondToTheSettings();
        _time.Advance(QueryReplyTimeout);
        await Poll();
        RespondToTheSettings();

        Assert.Empty(_switcher.GetIssues());

        _time.Advance(QueryReplyTimeout);
        await Poll();

        var issue = Assert.Single(_switcher.GetOngoingIssues());
        Assert.Equal("temperature-read", issue.Key);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Equal("The switcher did not answer Esc 20STAT (Temperature) 3 times in a row, the last was no 20Stat reply within 5 s", issue.Message);
        _mockClient.Verify(client => client.Send(TemperatureQuery), Times.Exactly(4));
        Respond("20Stat 36\r\n");
        Assert.Empty(_switcher.GetOngoingIssues());
        Assert.Equal("36", Detail("Temperature")?.Value);
    }

    [Fact]
    public async Task Heartbeat_ReadsEverySettingBetweenTheStatusAndTheTemperature()
    {
        SetConnection(ConnectionState.Connected);
        Respond("Inf01*IN1808\r\n");
        var sent = RecordSends();

        await Poll();

        Assert.Equal([..SettingsQueries, TemperatureQuery], sent.SkipWhile(command => command != "\u001bO2HDCP\r").Skip(1));
    }

    [Theory]
    [InlineData("SsavM1*1", "Screen Saver", "Black")]
    [InlineData("SsavM1*2", "Screen Saver", "BlueWithOsdText")]
    [InlineData("SsavM1*3", "Screen Saver", "UserImage")]
    [InlineData("SsavT1*501", "Sync Timeout", "Never")]
    [InlineData("SsavT1*30", "Sync Timeout", "30 s")]
    [InlineData("SsavT1*0", "Sync Timeout", "0 s")]
    [InlineData("HdcpN1*0", "HDCP Notification", "Black")]
    [InlineData("HdcpN1*1", "HDCP Notification", "GreenWithOsd")]
    [InlineData("HdcpN1*2", "HDCP Notification", "UserImage")]
    [InlineData("Ausw0", "Auto Switch", "Off")]
    [InlineData("Ausw1", "Auto Switch", "UserPriority")]
    [InlineData("Ausw2", "Auto Switch", "LastConnected")]
    [InlineData("Vmt1*0", "Video Mute 1A", "Unmuted")]
    [InlineData("Vmt2*1", "Video Mute 1B", "Video muted")]
    [InlineData("Vmt3*2", "Video Mute Loop", "Video and sync muted")]
    [InlineData("HdcpS1*0", "HDCP Mode 1A", "DisableAuthentication")]
    [InlineData("HdcpS2*1", "HDCP Mode 1B", "FollowInput")]
    [InlineData("HdcpS3*4", "HDCP Mode Loop", "AlwaysEncryptOutputContinuousTrials")]
    public void SettingReply_IsShownAsADetail(string reply, string label, string expected)
    {
        Respond($"{reply}\r\n");

        Assert.Equal(new DeviceDetail(label, expected), Detail(label));
    }

    [Fact]
    public void AllOutputsVideoMuteReply_SetsEveryOutput()
    {
        Respond("Vmt2\r\n");

        Assert.All(new[] { "Video Mute 1A", "Video Mute 1B", "Video Mute Loop" },
            label => Assert.Equal("Video and sync muted", Detail(label)?.Value));
    }

    [Fact]
    public void AutoSwitchTimeoutAndPriorityReplies_LeaveTheModeAlone()
    {
        Respond("Ausw1\r\nAuswT5\r\nAuswP8 7 6 5 4 3 2 1\r\n");

        Assert.Equal(new DeviceDetail("Auto Switch", "UserPriority"), Detail("Auto Switch"));
    }

    [Fact]
    public void UnrecognisedSettingReply_IsShownRaw()
    {
        Respond("SsavM1*7\r\n");

        Assert.Equal(new DeviceDetail("Screen Saver", "SsavM1*7", DetailTone.Warning), Detail("Screen Saver"));
    }

    [Fact]
    public async Task RejectedSettingQuery_ShowsItIsNotSupportedAndIsNotAskedAgain()
    {
        SetConnection(ConnectionState.Connected);
        Respond("Inf01*IN1808\r\n");
        foreach (var reply in ExtronBenchCaptures.In1808StatusReplies)
            Respond(reply);
        foreach (var reply in ExtronBenchCaptures.In1808IdentityReplies)
            Respond(reply);

        Respond("E10\r\n");

        Assert.Equal(new DeviceDetail("Screen Saver", "Not supported: E10"), Detail("Screen Saver"));
        Assert.Empty(_switcher.GetIssues());
        _mockClient.Invocations.Clear();
        await Poll();
        _mockClient.Verify(client => client.Send("\u001bM1SSAV\r"), Times.Never);
        _mockClient.Verify(client => client.Send("\u001bT1SSAV\r"), Times.Once);
    }

    [Fact]
    public async Task UnsetDesiredValues_NeverSendTheSettings()
    {
        using var release = new ManualResetEventSlim(true);
        var sent = RecordSends("", release);

        Respond("SsavM1*3\r\nSsavT1*0\r\nHdcpN1*0\r\nAusw2\r\nVmt2*2\r\nHdcpS1*4\r\n");
        _switcher.SetVideoMute(MuteState.Off);

        await Wait.UntilAsync(_time, VideoMuteStepGap, () => sent.Count == 3, "The video mute never reached output 3");
        Assert.Equal(["1*0B", "2*0B", "3*0B"], sent);
        Assert.Null(_switcher.DesiredScreenSaver);
        Assert.Null(_switcher.DesiredSyncTimeout);
        Assert.Null(_switcher.DesiredHdcpNotification);
        Assert.Null(_switcher.DesiredAutoSwitch);
        Assert.Null(_switcher.DesiredHdcpMode);
    }

    [Fact]
    public void Setters_SendTheDesiredValueAndKeepIt()
    {
        var sent = RecordSends();

        _switcher.SetScreenSaver(ExtronIn18Xx.ScreenSaverMode.UserImage);
        _switcher.SetSyncTimeout(30);
        _switcher.SetHdcpNotification(ExtronIn18Xx.HdcpNotificationMode.Black);
        _switcher.SetAutoSwitch(ExtronIn18Xx.AutoSwitchMode.LastConnected);
        _switcher.SetHdcpMode(ExtronIn18Xx.HdcpOutputMode.AlwaysEncryptOutput);

        Assert.Equal(
            [
                "\u001bM1*3SSAV\r", "\u001bT1*30SSAV\r", "\u001bN1*0HDCP\r", "\u001b2AUSW\r",
                "\u001bS1*2HDCP\r", "\u001bS2*2HDCP\r", "\u001bS3*2HDCP\r"
            ],
            sent);
        Assert.Equal(ExtronIn18Xx.ScreenSaverMode.UserImage, _switcher.DesiredScreenSaver);
        Assert.Equal(30, _switcher.DesiredSyncTimeout);
        Assert.Equal(ExtronIn18Xx.HdcpNotificationMode.Black, _switcher.DesiredHdcpNotification);
        Assert.Equal(ExtronIn18Xx.AutoSwitchMode.LastConnected, _switcher.DesiredAutoSwitch);
        Assert.Equal(ExtronIn18Xx.HdcpOutputMode.AlwaysEncryptOutput, _switcher.DesiredHdcpMode);
    }

    [Fact]
    public void DesiredValue_IsShownNextToTheReportedValue()
    {
        _switcher.SetAutoSwitch(ExtronIn18Xx.AutoSwitchMode.Off);

        Assert.Equal(new DeviceDetail("Auto Switch", "Not read yet, desired Off", DetailTone.Warning), Detail("Auto Switch"));

        Respond("Ausw0\r\n");

        Assert.Equal(new DeviceDetail("Auto Switch", "Off, desired Off"), Detail("Auto Switch"));
    }

    [Theory]
    [InlineData("SsavM1*1", "\u001bM1*2SSAV\r", "Screen Saver", "Black, desired BlueWithOsdText")]
    [InlineData("SsavT1*501", "\u001bT1*30SSAV\r", "Sync Timeout", "Never, desired 30 s")]
    [InlineData("HdcpS2*1", "\u001bS2*0HDCP\r", "HDCP Mode 1B", "FollowInput, desired DisableAuthentication")]
    public async Task MismatchedReply_SendsTheDesiredValue(string reply, string expectedCommand, string label, string expectedDetail)
    {
        _switcher.SetScreenSaver(ExtronIn18Xx.ScreenSaverMode.BlueWithOsdText);
        _switcher.SetSyncTimeout(30);
        _switcher.SetHdcpMode(ExtronIn18Xx.HdcpOutputMode.DisableAuthentication);
        using var release = new ManualResetEventSlim(true);
        var sent = RecordSends("", release);

        Respond($"{reply}\r\n");

        await Wait.UntilAsync(_time, ReconcileGap, () => sent.Count == 1, "The desired value was never sent");
        Assert.Equal([expectedCommand], sent);
        Assert.Equal(new DeviceDetail(label, expectedDetail, DetailTone.Warning), Detail(label));
        Assert.Contains(_switcher.Events, e => e.Type == EventType.DriverState && e.Info.StartsWith($"Reconciling {label}"));
    }

    [Fact]
    public async Task MismatchedVideoMute_IsSentToThatOutputOnly()
    {
        using var release = new ManualResetEventSlim(true);
        var sent = RecordSends("", release);
        _switcher.SetVideoMute(MuteState.On);
        await Wait.UntilAsync(_time, VideoMuteStepGap, () => sent.Count == 3, "The video mute never reached output 3");

        Respond("Vmt1*2\r\nVmt2*0\r\nVmt3*2\r\n");

        await Wait.UntilAsync(_time, ReconcileGap, () => sent.Count == 4, "The desired mute was never sent to output 2");
        Assert.Equal(["1*2B", "2*2B", "3*2B", "2*2B"], sent);
        Assert.Equal(MuteState.On, _switcher.DesiredVideoMute);
    }

    [Fact]
    public async Task MatchingReply_SendsNothing()
    {
        _switcher.SetHdcpNotification(ExtronIn18Xx.HdcpNotificationMode.UserImage);
        using var release = new ManualResetEventSlim(true);
        var sent = RecordSends("", release);

        Respond("HdcpN1*2\r\n");
        _switcher.SetVideoMute(MuteState.Off);

        await Wait.UntilAsync(_time, VideoMuteStepGap, () => sent.Count == 3, "The video mute never reached output 3");
        Assert.Equal(["1*0B", "2*0B", "3*0B"], sent);
        Assert.Equal(new DeviceDetail("HDCP Notification", "UserImage, desired UserImage"), Detail("HDCP Notification"));
    }

    [Fact]
    public async Task ClearedDesiredValue_StopsReconcilingAndSendsNothing()
    {
        _switcher.SetAutoSwitch(ExtronIn18Xx.AutoSwitchMode.LastConnected);
        using var release = new ManualResetEventSlim(true);
        var sent = RecordSends("", release);

        _switcher.SetAutoSwitch(null);
        _switcher.SetVideoMute(MuteState.Unknown);
        Respond("Ausw0\r\nVmt1*2\r\n");
        _switcher.SetVideoMute(MuteState.Off);

        await Wait.UntilAsync(_time, VideoMuteStepGap, () => sent.Count == 3, "The video mute never reached output 3");
        Assert.Equal(["1*0B", "2*0B", "3*0B"], sent);
        Assert.Null(_switcher.DesiredAutoSwitch);
        Assert.Equal(new DeviceDetail("Auto Switch", "Off"), Detail("Auto Switch"));
    }
}

[Collection(LoggerFactoryCollection.Name)]
public class ExtronIn18XxLogTest
{
    private readonly Mock<CommunicationClient> _client = TestFactory.CreateCommunicationClient();
    private readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly ExtronIn18Xx _switcher;

    public ExtronIn18XxLogTest()
    {
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = _logs;
        try
        {
            _switcher = new ExtronIn18Xx(_client.Object, 8, "Logged matrix");
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }

    private void Respond(string response) => _client.Object.ResponseHandlers!.Invoke(response);

    [Fact]
    public void Routes_AfterAModelReplyWithFewerInputs_UseTheDiscoveredCountAndWarnOnce()
    {
        Respond("Inf01*IN1806\r\n");
        _client.Invocations.Clear();

        _switcher.RouteAV(6, 1);
        _switcher.RouteAV(7, 1);
        _switcher.RouteVideo(8, 1);
        _switcher.RouteAudio(7, 1);

        _client.Verify(x => x.Send("6*1!"), Times.Once);
        _client.Verify(x => x.Send(It.IsAny<string>()), Times.Once);
        Assert.Single(_logs.Logs, log => log.Message.Contains("reports 6 inputs") && log.Message.Contains("constructed with 8"));
        Assert.Contains(_switcher.Events, e => e.Type == EventType.Error && e.Info.EndsWith("must be between 1 and 6"));
    }

    [Fact]
    public void ModelReply_MatchingTheConstructorsInputCount_DoesNotWarn()
    {
        Respond("Inf01*IN1808\r\n");

        Assert.DoesNotContain(_logs.Logs, log => log.Message.Contains("constructed with"));
    }

    [Theory]
    [InlineData("IN00 1*1*1")]
    [InlineData("IN00 1*1*1*1*1*1*1*1*1")]
    public void SignalPresence_ThatDoesNotCoverEveryInput_IsLoggedAndIgnored(string response)
    {
        Respond("Inf01*IN1808\r\n");

        Respond(response);

        Assert.Contains(_logs.Logs, log => log.Message.Contains(response[4..]) && log.Message.Contains("8 inputs"));
        Assert.Equal(8, _switcher.NumberOfInputs);
        Assert.All(_switcher.GetInputs(), input => Assert.Equal(ConnectionState.Unknown, input.InputConnectionStatus));
    }

    [Theory]
    [InlineData("HdcpI00*2")]
    [InlineData("HdcpI09*2")]
    [InlineData("HdcpI02")]
    public void InputHdcp_ForAPortOutsideTheInputs_IsLoggedAndIgnored(string response)
    {
        Respond("Inf01*IN1808\r\n");

        Respond(response);

        Assert.Contains(_logs.Logs, log => log.Message.Contains($"HDCP status {response[5..]} "));
        Assert.All(_switcher.GetInputs(), input => Assert.Equal(HdcpStatus.Unknown, input.InputHdcpStatus));
    }

    [Fact]
    public void UnrecognisedSettingReply_IsLoggedRawOnceInTheDriversScope()
    {
        Respond("SsavM1*7\r\n");
        Respond("SsavM1*7\r\n");

        var log = Assert.Single(_logs.Logs, log => log.Message.Contains("SsavM1*7"));
        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Equal(nameof(ExtronIn18Xx), log.Scope["Class"]);
    }

    [Fact]
    public async Task MismatchedReply_IsLoggedWithTheSettingTheReplyAndTheDesiredValue()
    {
        _switcher.SetScreenSaver(ExtronIn18Xx.ScreenSaverMode.BlueWithOsdText);
        _client.Invocations.Clear();

        Respond("SsavM1*1\r\n");

        var log = Assert.Single(_logs.Logs, log => log.Message.StartsWith("Reconciling"));
        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Contains("Screen Saver", log.Message);
        Assert.Contains("SsavM1*1", log.Message);
        Assert.Contains("BlueWithOsdText", log.Message);
        Assert.Equal(nameof(ExtronIn18Xx), log.Scope["Class"]);
        await Wait.UntilAsync(() => _client.Invocations.Any(invocation => Equals(invocation.Arguments[0], "\u001bM1*2SSAV\r")),
            "The desired screen saver was never sent");
    }

    [Fact]
    public void MatchingReply_IsNotLogged()
    {
        _switcher.SetScreenSaver(ExtronIn18Xx.ScreenSaverMode.Black);

        Respond("SsavM1*1\r\n");

        Assert.DoesNotContain(_logs.Logs, log => log.Message.Contains("Screen Saver"));
    }
}

[Collection(LoggerFactoryCollection.Name)]
public class ExtronIn18XxBenchTest
{
    private static readonly TimeSpan ConnectStepGap = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);
    private const string RouteTrigger = "5d30414a";
    private readonly FakeTimeProvider _time = new();
    private readonly Mock<SshClient> _client = TestFactory.CreateSshClient();
    private readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly ExtronIn18Xx _switcher;

    public ExtronIn18XxBenchTest()
    {
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = _logs;
        try
        {
            _switcher = new ExtronIn18Xx(_client.Object, 8, "Bench matrix", _time);
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
        await Wait.UntilAsync(_time, ConnectStepGap,
            () => _client.Invocations.Any(invocation => Equals(invocation.Arguments[0], "1I")), "The model was never asked for");
        Respond([..ExtronBenchCaptures.In1808ConnectReplies, ..ExtronBenchCaptures.In1808StatusReplies, ..ExtronIn18XxTest.SettingsReplies]);
    }

    private Task Poll() => (Task)typeof(ExtronIn18Xx)
        .GetMethod("Poll", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(_switcher, [CancellationToken.None])!;

    private DeviceDetail? Detail(string label) => _switcher.Details.SingleOrDefault(detail => detail.Label == label);

    [Fact]
    public async Task BenchSequence_MatchesEveryReplyToTheCommandItAnswers()
    {
        await Connect();
        Respond(ExtronBenchCaptures.In1808IdentityReplies);
        await Poll();
        Respond([..ExtronBenchCaptures.In1808StatusReplies, ..ExtronIn18XxTest.SettingsReplies, ExtronBenchCaptures.In1808TemperatureReply]);
        _time.Advance(ReplyTimeout);

        await Poll();

        Assert.Equal(["IN1808", "1.09.0006", "60-1615-01", "ROOM-VID-SWI01", "00-05-A6-00-00-02", "46"],
            new[] { "Model", "Firmware", "Part Number", "Unit Name", "MAC", "Temperature" }.Select(label => Detail(label)?.Value));
        Assert.Equal(ConnectionState.Connected, _switcher.Inputs[1].InputConnectionStatus);
        Assert.DoesNotContain(_logs.Logs, log => log.Message.StartsWith("The switcher didn't answer"));
        Assert.DoesNotContain(_logs.Logs, log => log.Message.Contains("asking again"));
        Assert.DoesNotContain(_logs.Logs, log => log.Level >= LogLevel.Warning);
        Assert.Empty(_switcher.GetIssues());
    }

    [Fact]
    public async Task RejectedRoute_WhileTheTemperatureQueryIsPending_IsReportedAndTheQueryKeepsItsReply()
    {
        await Connect();
        Respond(ExtronBenchCaptures.In1808IdentityReplies);
        using (Trigger.Resume(RouteTrigger))
            _switcher.RouteAV(4, 1);
        await Poll();

        Respond(["E13", ..ExtronBenchCaptures.In1808StatusReplies, ExtronBenchCaptures.In1808TemperatureReply]);

        var issue = Assert.Single(_switcher.GetIssues());
        Assert.Equal(("rejected-command", IssueStatus.Momentary, "The switcher rejected 4*1! with E13"), (issue.Key, issue.Status, issue.Message));
        Assert.Equal(RouteTrigger, Assert.Single(_logs.Logs, log => log.Level == LogLevel.Warning).TriggerId);
        Assert.Equal(new DeviceDetail("Temperature", "46"), Detail("Temperature"));
    }

    [Fact]
    public async Task EmptyUnitName_ShowsTheRawReplyAndCountsAsAnswered()
    {
        await Connect();
        Respond([ExtronBenchCaptures.In1808IdentityReplies[0], ExtronBenchCaptures.In1808IdentityReplies[1], "Ipn ",
            ExtronBenchCaptures.In1808IdentityReplies[3]]);
        _time.Advance(ReplyTimeout);
        _client.Invocations.Clear();

        await Poll();

        Assert.Equal(new DeviceDetail("Unit Name", "Ipn "), Detail("Unit Name"));
        Assert.DoesNotContain(_client.Invocations, invocation => Equals(invocation.Arguments[0], "\u001bCN\r"));
        Assert.DoesNotContain(_logs.Logs, log => log.Message.Contains("asking again"));
    }
}
