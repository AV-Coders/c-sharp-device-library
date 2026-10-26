using System.Collections.Concurrent;
using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AVCoders.Matrix.Tests;

public class ExtronIn16xxTest
{
    private static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReturnTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectStepGap = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan VideoMuteStepGap = TimeSpan.FromMilliseconds(300);
    private readonly FakeTimeProvider _time = new();
    private readonly ExtronIn16Xx _switcher;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();

    public ExtronIn16xxTest()
    {
        _switcher = new ExtronIn16Xx(_mockClient.Object, 6, "test matrix", _time);
    }

    private void SetConnection(ConnectionState state) => typeof(CommunicationClient)
        .GetProperty(nameof(CommunicationClient.ConnectionState))!.SetValue(_mockClient.Object, state);

    private void Respond(string response) => _mockClient.Object.ResponseHandlers!.Invoke(response);

    private Task Poll() => (Task)typeof(ExtronIn16Xx)
        .GetMethod("Poll", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(_switcher, [CancellationToken.None])!;

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
    [InlineData(1, "1%")]
    [InlineData(6, "6%")]
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
    [InlineData(1, "1$")]
    [InlineData(6, "6$")]
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
    [InlineData(1, "1!")]
    [InlineData(6, "6!")]
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
    public void SetSyncTimeout_SendsTheCommand()
    {
        string expectedCommand = "\u001bT0SSAV\r";
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
        string expectedCommand = "\u001bT501SSAV\r";
        _switcher.SetSyncTimeout(501);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void SignalPresence_DiscoversTheInputsAndSetsTheirSignalState()
    {
        Respond("In00 1*0*0*1*0*1*0*0\r\n");

        Assert.Equal(8, _switcher.NumberOfInputs);
        Assert.Equal(Enumerable.Range(1, 8), _switcher.Inputs.Select(input => input.Number));
        Assert.Equal(
            [
                ConnectionState.Connected, ConnectionState.Disconnected, ConnectionState.Disconnected, ConnectionState.Connected,
                ConnectionState.Disconnected, ConnectionState.Connected, ConnectionState.Disconnected, ConnectionState.Disconnected
            ],
            _switcher.GetInputs().Select(input => input.InputConnectionStatus));
    }

    [Fact]
    public void SignalPresence_KeepsTheInputsAndNotifiesSubscribersOnlyWhenTheCountChanges()
    {
        var seen = new List<int>();
        _switcher.EndpointsChangedHandlers += () => seen.Add(_switcher.GetInputs().Count);
        Respond("In00 1*0*0*0*0*0\r\n");
        var first = _switcher.GetInputs()[0];

        Respond("In00 0*0*0*0*0*0\r\n");

        Assert.Equal([6], seen);
        Assert.Same(first, _switcher.GetInputs()[0]);
        Assert.Equal(ConnectionState.Disconnected, first.InputConnectionStatus);
    }

    [Theory]
    [InlineData("In1 All")]
    [InlineData("Vid1 Aud1 Typ6")]
    [InlineData("Reconfig")]
    public void OtherResponses_LeaveTheInputsAlone(string response)
    {
        Respond(response);

        Assert.Empty(_switcher.GetInputs());
    }

    [Fact]
    public async Task Heartbeat_QueriesSignalPresenceOnlyWhileConnected()
    {
        await Poll();
        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);

        SetConnection(ConnectionState.Connected);
        await Wait.UntilAsync(_time, ConnectStepGap,
            () => _mockClient.Invocations.Any(invocation => Equals(invocation.Arguments[0], "\u001b0LS\r")),
            "The signal presence was never asked for");
        _mockClient.Invocations.Clear();
        await Poll();

        _mockClient.Verify(x => x.Send("\u001bCV\r"), Times.Once);
        _mockClient.Verify(x => x.Send("\u001b0LS\r"), Times.Once);
    }

    [Fact]
    public async Task Connect_EnablesVerboseModeThenRequestsInformationWithoutBlockingTheClient()
    {
        using var release = new ManualResetEventSlim();
        var sent = RecordSends("\u001b3CV\r", release);

        var connect = Task.Run(() => SetConnection(ConnectionState.Connected));

        await connect.WaitAsync(ReturnTimeout);
        release.Set();
        await Wait.UntilAsync(_time, ConnectStepGap, () => sent.Count == 11, "The output connectors were never asked for");
        Assert.Equal(
            [
                "\u001b3CV\r", "I", "\u001b0LS\r", "!",
                "\u001bI3HDCP\r", "\u001bI4HDCP\r", "\u001bI5HDCP\r", "\u001bI6HDCP\r",
                "\u001bO1HDCP\r", "\u001bO2HDCP\r", "\u001bO3HDCP\r"
            ],
            sent);
    }

    [Fact]
    public async Task Heartbeat_QueriesTheTieAndTheHdmiInputsAndEveryOutputConnector()
    {
        SetConnection(ConnectionState.Connected);
        await Wait.UntilAsync(_time, ConnectStepGap,
            () => _mockClient.Invocations.Any(invocation => Equals(invocation.Arguments[0], "\u001bO3HDCP\r")),
            "The output connectors were never asked for");
        _mockClient.Invocations.Clear();

        await Poll();

        _mockClient.Verify(x => x.Send("!"), Times.Once);
        _mockClient.Verify(x => x.Send("\u001bI1HDCP\r"), Times.Never);
        _mockClient.Verify(x => x.Send("\u001bI2HDCP\r"), Times.Never);
        foreach (var input in Enumerable.Range(3, 4))
            _mockClient.Verify(x => x.Send($"\u001bI{input}HDCP\r"), Times.Once);
        foreach (var connector in Enumerable.Range(1, 3))
            _mockClient.Verify(x => x.Send($"\u001bO{connector}HDCP\r"), Times.Once);
    }

    [Fact]
    public void Outputs_AreTheThreeConnectorsOfTheOneOutput()
    {
        Assert.Equal(1, _switcher.NumberOfOutputs);
        Assert.Equal(["Output 1A", "Output 1B", "Output 1C"], _switcher.GetOutputs().Select(output => output.Name));
    }

    [Theory]
    [InlineData("In03 All\r\n", "3")]
    [InlineData("In3 All\r\n", "3")]
    [InlineData("In05 RGB\r\n", "5")]
    public void VideoTie_SetsTheSourceOnEveryOutputConnector(string response, string input)
    {
        Respond(response);

        Assert.All(_switcher.GetOutputs(), output => Assert.Equal(input, output.StreamAddress));
    }

    [Fact]
    public void VideoTie_NamesTheDiscoveredInput()
    {
        Respond("In00 0*0*1*1*0*0*0*0\r\n");

        Respond("In03 All\r\n");

        var routed = _switcher.GetOutputs()[0].StreamAddress;
        Assert.Same(_switcher.Inputs[2], _switcher.GetInputs().Find(input => input.StreamAddress == routed));
    }

    [Fact]
    public void AudioTie_LeavesTheVideoSourceAlone()
    {
        Respond("In03 All\r\n");

        Respond("In04 Aud\r\n");

        Assert.All(_switcher.GetOutputs(), output => Assert.Equal("3", output.StreamAddress));
    }

    [Fact]
    public void OutputHdcp_SetsEachConnectorsSinkAndHdcp()
    {
        Respond("HdcpO1*2\r\nHdcpO2*0\r\nHdcpO3*1\r\n");

        var outputs = _switcher.GetOutputs();
        Assert.Equal(
            [ConnectionState.Connected, ConnectionState.Disconnected, ConnectionState.Connected],
            outputs.Select(output => output.OutputConnectionStatus));
        Assert.Equal(
            [HdcpStatus.NotSupported, HdcpStatus.Unknown, HdcpStatus.Available],
            outputs.Select(output => output.OutputHdcpStatus));
    }

    [Fact]
    public void OutputHdcp_ForAConnectorItDoesNotHave_IsIgnored()
    {
        Respond("HdcpO4*1\r\n");

        Assert.All(_switcher.GetOutputs(), output => Assert.Equal(ConnectionState.Unknown, output.OutputConnectionStatus));
    }

    [Fact]
    public void InputHdcp_SetsTheHdcpAndLeavesTheSignalToSignalPresence()
    {
        Respond("In00 0*0*1*1*0*0*0*0\r\n");

        Respond("HdcpI03*2\r\nHdcpI04*1\r\nHdcpI05*2\r\n");

        Assert.Equal(HdcpStatus.NotSupported, _switcher.Inputs[2].InputHdcpStatus);
        Assert.Equal(HdcpStatus.Active, _switcher.Inputs[3].InputHdcpStatus);
        Assert.Equal(ConnectionState.Disconnected, _switcher.Inputs[4].InputConnectionStatus);
    }

    [Fact]
    public void InputHdcp_BeforeTheInputsAreDiscovered_IsIgnored()
    {
        Respond("HdcpI03*2\r\n");

        Assert.Empty(_switcher.GetInputs());
    }

    [Fact]
    public void SignalPresence_QueriesTheInputNamesOnlyWhenItDiscoversInputs()
    {
        Respond("In00 0*0*1*1*0*1*0*0\r\n");
        Respond("In00 0*0*1*1*0*1*0*0\r\n");

        foreach (var input in Enumerable.Range(1, 8))
            _mockClient.Verify(x => x.Send($"\u001b{input}NI\r"), Times.Once);
    }

    [Fact]
    public void InputNames_RenameTheInputs()
    {
        Respond("In00 0*0*1*1*0*1*0*0\r\n");

        Respond("Nmi01,Input 1\r\nNmi02,Input 2\r\nNmi03,Codec\r\nNmi04,BLURAY\r\nNmi05,DOCCAM\r\nNmi06,SHARELINK\r\nNmi07,Input 7\r\nNmi08,BAR LAPTOP\r\n");

        Assert.Equal(
            ["Input 1", "Input 2", "Codec", "BLURAY", "DOCCAM", "SHARELINK", "Input 7", "BAR LAPTOP"],
            _switcher.GetInputs().Select(input => input.Name));
    }

    [Fact]
    public void InputName_BeforeTheInputsAreDiscovered_IsIgnored()
    {
        Respond("Nmi03,Codec\r\n");

        Assert.Empty(_switcher.GetInputs());
    }

    [Fact]
    public async Task Reconnect_QueriesTheNamesOfTheKnownInputs()
    {
        Respond("In00 0*0*1*1*0*1*0*0\r\n");
        _mockClient.Invocations.Clear();

        SetConnection(ConnectionState.Connected);
        await Wait.UntilAsync(_time, ConnectStepGap,
            () => _mockClient.Invocations.Any(invocation => Equals(invocation.Arguments[0], "\u001b8NI\r")),
            "The input names were never asked for");

        foreach (var input in Enumerable.Range(1, 8))
            _mockClient.Verify(x => x.Send($"\u001b{input}NI\r"), Times.Once);
    }

    [Fact]
    public void Hotplug_QueriesThatConnectorsHdcp()
    {
        Respond("HplgO2\r\n");

        _mockClient.Verify(x => x.Send("\u001bO2HDCP\r"), Times.Once);
    }

    [Fact]
    public void Reconfig_QueriesTheSignalPresence()
    {
        Respond("Reconfig\r\n");

        _mockClient.Verify(x => x.Send("\u001b0LS\r"), Times.Once);
    }

    [Fact]
    public void Disconnect_ClearsTheSignalHdcpAndSource()
    {
        Respond("In00 0*0*1*1*0*0*0*0\r\nHdcpI03*2\r\nHdcpO1*2\r\nIn03 All\r\n");

        SetConnection(ConnectionState.Disconnected);

        Assert.All(_switcher.Inputs, input =>
        {
            Assert.Equal(ConnectionState.Unknown, input.InputConnectionStatus);
            Assert.Equal(HdcpStatus.Unknown, input.InputHdcpStatus);
        });
        Assert.All(_switcher.Outputs, output =>
        {
            Assert.Equal(ConnectionState.Unknown, output.OutputConnectionStatus);
            Assert.Equal(HdcpStatus.Unknown, output.OutputHdcpStatus);
            Assert.Equal(string.Empty, output.StreamAddress);
        });
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
}
