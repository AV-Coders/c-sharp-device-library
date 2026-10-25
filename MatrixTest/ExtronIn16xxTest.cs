using System.Collections.Concurrent;
using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Matrix.Tests;

public class ExtronIn16xxTest
{
    private static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReturnTimeout = TimeSpan.FromSeconds(5);
    private readonly ExtronIn16Xx _switcher;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();

    public ExtronIn16xxTest()
    {
        _switcher = new ExtronIn16Xx(_mockClient.Object, 6, "test matrix");
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
    [InlineData(1, "1%")]
    [InlineData(2, "2%")]
    [InlineData(3, "3%")]
    [InlineData(4, "4%")]
    [InlineData(5, "5%")]
    [InlineData(6, "6%")]
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
        string expectedRouteCommand = "3%";
        _switcher.RouteVideo(3, 6);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Theory]
    [InlineData(1, "1$")]
    [InlineData(2, "2$")]
    [InlineData(3, "3$")]
    [InlineData(4, "4$")]
    [InlineData(5, "5$")]
    [InlineData(6, "6$")]
    public void RouteAudio_SendsTheCommand(int input, string expectedRouteCommand)
    {
        _switcher.RouteAudio(input, 0);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void RouteAudio_IgnoresOutputParameter()
    {
        string expectedRouteCommand = "1$";
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
    [InlineData(1, "1!")]
    [InlineData(2, "2!")]
    [InlineData(3, "3!")]
    [InlineData(4, "4!")]
    [InlineData(5, "5!")]
    [InlineData(6, "6!")]
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
        string expectedRouteCommand = "2!";
        _switcher.RouteAV(2, 3);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void SetSyncTimeout_SendsTheCommand()
    {
        string expectedCommand = "\u001bT0SSAV\r";
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
        string expectedCommand = "\u001bT501SSAV\r";
        _switcher.SetSyncTimeout(501);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void SetSyncTimeout_StatesTheLimitItEnforces()
    {
        _switcher.SetSyncTimeout(502);

        Assert.Contains(_switcher.Events,
            e => e.Type == EventType.Error && e.Info == "The sync timeout can't be longer than 501 seconds");
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
        await Wait.UntilAsync(() => _mockClient.Invocations.Any(invocation => Equals(invocation.Arguments[0], "\u001b0LS\r")),
            "The signal presence was never asked for");
        _mockClient.Invocations.Clear();
        await Poll();

        _mockClient.Verify(x => x.Send("\u001bCV\r"), Times.Once);
        _mockClient.Verify(x => x.Send("\u001b0LS\r"), Times.Once);
    }

    [Fact]
    public void SupportsAudioBreakaway_IsTrueAsAudioTiesAreSent()
    {
        Assert.True(_switcher.SupportsAudioBreakaway);
    }

    [Fact]
    public async Task Connect_EnablesVerboseModeThenRequestsInformationWithoutBlockingTheClient()
    {
        using var release = new ManualResetEventSlim();
        var sent = RecordSends("\u001b3CV\r", release);

        var connect = Task.Run(() => SetConnection(ConnectionState.Connected));

        await connect.WaitAsync(ReturnTimeout);
        release.Set();
        await Wait.UntilAsync(() => sent.Count == 3, "The signal presence was never asked for");
        Assert.Equal(["\u001b3CV\r", "I", "\u001b0LS\r"], sent);
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