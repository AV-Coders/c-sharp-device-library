using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AVCoders.Matrix.Tests;

public class ExtronSwTest : IDisposable
{
    private static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReturnTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan QueryReplyTimeout = TimeSpan.FromSeconds(5);
    private static readonly string[] IdentityQueries = ["Q", "N", "\u001bCN\r", "\u001bCH\r"];
    private readonly FakeTimeProvider _time = new();
    private readonly ExtronSw _switcher;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();

    public ExtronSwTest()
    {
        _switcher = new ExtronSw(_mockClient.Object, "Test switch", _time);
        Respond("Inf01*SW4 HD 4K PLUS Series");
        _mockClient.Invocations.Clear();
    }

    public void Dispose() => _switcher.Dispose();

    private void Respond(string response) => _mockClient.Object.ResponseHandlers!.Invoke(response);

    private void SetConnection(ConnectionState state) => typeof(CommunicationClient)
        .GetProperty(nameof(CommunicationClient.ConnectionState))!.SetValue(_mockClient.Object, state);

    private Task Poll() => (Task)typeof(ExtronSw)
        .GetMethod("Poll", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(_switcher, [CancellationToken.None])!;

    private DeviceDetail? Detail(string label) => _switcher.Details.SingleOrDefault(detail => detail.Label == label);

    private static void HoldFirstInputChange(SyncStatus input, ManualResetEventSlim entered, ManualResetEventSlim release)
    {
        var changes = 0;
        input.InputStatusChangedHandlers += (_, _, _) =>
        {
            if (Interlocked.Increment(ref changes) != 1)
                return;
            entered.Set();
            release.Wait(HoldTimeout);
        };
    }

    [Fact]
    public void ConnectAndReconnect_EnableVerboseAndQueryStatus()
    {
        SetConnection(ConnectionState.Connected);
        SetConnection(ConnectionState.Disconnected);
        SetConnection(ConnectionState.Connected);

        foreach (var command in new[] { "\u001b3CV\r", "1I", "\u001bLS\r", "\u001bIHDCP\r", "\u001bOHDCP\r", "!" })
            _mockClient.Verify(client => client.Send(command), Times.Exactly(2));
    }

    [Fact]
    public void Constructor_InitializesAnAlreadyConnectedClient()
    {
        var client = TestFactory.CreateCommunicationClient();
        typeof(CommunicationClient).GetProperty(nameof(CommunicationClient.ConnectionState))!
            .SetValue(client.Object, ConnectionState.Connected);
        using var switcher = new ExtronSw(client.Object, "Auto discovery", _time);
        client.Verify(c => c.Send("\u001b3CV\r"), Times.Once);
        client.Verify(c => c.Send("1I"), Times.Once);
        Assert.Empty(switcher.GetInputs());
        Assert.Single(switcher.GetOutputs());
        client.Object.ResponseHandlers!("Inf01*SW8 HD 4K PLUS Series");
        Assert.Equal(8, switcher.NumberOfInputs);
    }

    [Fact]
    public async Task Heartbeat_RefreshesStatusOnlyWhileConnected()
    {
        await Poll();
        _mockClient.Verify(c => c.Send(It.IsAny<string>()), Times.Never);
        SetConnection(ConnectionState.Connected);
        _mockClient.Invocations.Clear();
        await Poll();
        foreach (var command in new[] { "\u001bLS\r", "\u001bIHDCP\r", "\u001bOHDCP\r", "!" })
            _mockClient.Verify(c => c.Send(command), Times.Once);
        SetConnection(ConnectionState.Disconnected);
        _mockClient.Invocations.Clear();
        await Poll();
        _mockClient.Verify(c => c.Send(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void ModelResponse_DiscoversAllModelsAndPreservesExistingEndpoints(int count)
    {
        var first = _switcher.GetInputs()[0];
        var changes = 0;
        _switcher.EndpointsChangedHandlers += () => changes++;
        Respond($"Inf01*SW{count} HD 4K PLUS Series\r\n");
        Respond($"Inf01*SW{count} HD 4K PLUS Series");
        Assert.Equal(count, _switcher.NumberOfInputs);
        Assert.Equal(count, _switcher.GetInputs().Count);
        Assert.Same(first, _switcher.GetInputs()[0]);
        Assert.Equal(count == 4 ? 0 : 1, changes);
        Assert.Equal(1, _switcher.NumberOfOutputs);
        Assert.Equal(AVEndpointType.Decoder, Assert.Single(_switcher.GetOutputs()).DeviceType);
    }

    [Fact]
    public void SignalResponse_UpdatesEveryConnectorAndNotifiesSubscribers()
    {
        var inputs = _switcher.GetInputs();
        var output = Assert.Single(_switcher.GetOutputs());
        var inputChanges = 0;
        var outputChanges = 0;
        inputs[0].InputStatusChangedHandlers += (_, _, _) => inputChanges++;
        output.OutputStatusChangedHandlers += (_, _, _) => outputChanges++;
        Respond("Sig1 0 1 0*1\r\n");
        Assert.Equal(ConnectionState.Connected, inputs[0].InputConnectionStatus);
        Assert.Equal(ConnectionState.Disconnected, inputs[1].InputConnectionStatus);
        Assert.Equal(ConnectionState.Connected, inputs[2].InputConnectionStatus);
        Assert.Equal(ConnectionState.Disconnected, inputs[3].InputConnectionStatus);
        Assert.Equal(ConnectionState.Connected, output.OutputConnectionStatus);
        Respond("Sig0 0 0 0*0");
        Assert.Equal(ConnectionState.Disconnected, output.OutputConnectionStatus);
        Assert.Equal(2, inputChanges);
        Assert.Equal(2, outputChanges);
    }

    [Fact]
    public void HdcpResponse_UsesSwStatusCodesAndLeavesSignalStateIndependent()
    {
        Respond("Sig1 1 0 1*1\r\nHdcp I1 2 0 1\r\nHdcp O1\r\n");
        var inputs = _switcher.GetInputs();
        var output = Assert.Single(_switcher.GetOutputs());
        Assert.Equal(HdcpStatus.Active, inputs[0].InputHdcpStatus);
        Assert.Equal(HdcpStatus.NotSupported, inputs[1].InputHdcpStatus);
        Assert.Equal(HdcpStatus.Unknown, inputs[2].InputHdcpStatus);
        Assert.Equal(HdcpStatus.Available, output.OutputHdcpStatus);
        Respond("HdcpO2");
        Assert.Equal(HdcpStatus.NotSupported, output.OutputHdcpStatus);
        Respond("HdcpO0");
        Assert.Equal(HdcpStatus.Unknown, output.OutputHdcpStatus);
        Assert.Equal(ConnectionState.Connected, output.OutputConnectionStatus);
    }

    [Theory]
    [InlineData("In2 All", "2")]
    [InlineData("IN 03 All", "3")]
    [InlineData("In0 All", "0")]
    [InlineData("In4 Ausw0 Afmt0 Vmt0", "4")]
    public void TieFeedback_UpdatesOutputStreamAddress(string response, string selected)
    {
        Respond(response);
        Assert.Equal(selected, Assert.Single(_switcher.GetOutputs()).StreamAddress);
    }

    [Theory]
    [InlineData("Sig1 0 x 0*1")]
    [InlineData("Sig1 0 1 0*2")]
    [InlineData("Sig1 0 1*1")]
    [InlineData("Sig1 0 1 0")]
    [InlineData("Hdcp I1 2 3 1")]
    [InlineData("Hdcp I1 2")]
    [InlineData("In99999999999999999999 All")]
    [InlineData("Inf01*SW9 HD 4K PLUS Series")]
    public void InvalidResponses_DoNotChangeStatus(string response)
    {
        Respond(response);
        Assert.Equal(4, _switcher.NumberOfInputs);
        Assert.All(_switcher.GetInputs(), input =>
        {
            Assert.Equal(ConnectionState.Unknown, input.InputConnectionStatus);
            Assert.Equal(HdcpStatus.Unknown, input.InputHdcpStatus);
        });
        Assert.Equal(ConnectionState.Unknown, _switcher.GetOutputs()[0].OutputConnectionStatus);
        Assert.Empty(_switcher.GetOutputs()[0].StreamAddress);
    }

    [Fact]
    public async Task ConnectionChange_IsNotHeldUpByASlowSubscriberOnTheReceiveThread()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        HoldFirstInputChange(_switcher.GetInputs()[0], entered, release);
        var receive = Task.Run(() => Respond("Sig1 0 1 0*1"));
        Assert.True(entered.Wait(ReturnTimeout), "The subscriber was never called");

        var disconnect = Task.Run(() => SetConnection(ConnectionState.Disconnected));

        await disconnect.WaitAsync(ReturnTimeout);
        release.Set();
        await receive;
    }

    [Fact]
    public async Task Dispose_DuringAResponse_StopsTheRestOfItCreatingEndpoints()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        HoldFirstInputChange(_switcher.GetInputs()[0], entered, release);
        var receive = Task.Run(() => Respond("Sig1 0 1 0*1\r\nInf01*SW8 HD 4K PLUS Series"));
        Assert.True(entered.Wait(ReturnTimeout), "The subscriber was never called");

        var dispose = Task.Run(_switcher.Dispose);

        await dispose.WaitAsync(ReturnTimeout);
        release.Set();
        await receive;
        Assert.Empty(_switcher.GetInputs());
        Assert.Empty(_switcher.GetOutputs());
    }

    [Fact]
    public void Dispose_DeregistersAndClearsTheEndpoints()
    {
        var inputs = _switcher.GetInputs();
        var output = _switcher.GetOutputs()[0];

        _switcher.Dispose();

        Assert.Empty(_switcher.GetInputs());
        Assert.Empty(_switcher.GetOutputs());
        Assert.DoesNotContain(LogBaseRegistry.GetAll(), instance => inputs.Contains(instance) || instance == output);
    }

    [Fact]
    public void Disconnect_ClearsStaleStatusWithoutReplacingEndpoints()
    {
        SetConnection(ConnectionState.Connected);
        Respond("Sig1 1 1 1*1\r\nHdcp I1 1 1 1\r\nIn2 All");
        var input = _switcher.GetInputs()[0];
        SetConnection(ConnectionState.Disconnected);
        Assert.Same(input, _switcher.GetInputs()[0]);
        Assert.Equal(ConnectionState.Unknown, input.InputConnectionStatus);
        Assert.Equal(HdcpStatus.Unknown, input.InputHdcpStatus);
        Assert.Empty(_switcher.GetOutputs()[0].StreamAddress);
    }

    [Fact]
    public async Task Dispose_UnsubscribesAndStopsHeartbeat()
    {
        SetConnection(ConnectionState.Connected);
        _switcher.Dispose();
        _mockClient.Invocations.Clear();
        await Poll();
        Assert.Null(_mockClient.Object.ResponseHandlers);
        Assert.Null(_mockClient.Object.ConnectionStateHandlers);
        _mockClient.Verify(c => c.Send(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(1, "1!")]
    [InlineData(4, "4!")]
    public void RouteAV_SendsTheCommandWhateverTheOutput(int input, string expectedRouteCommand)
    {
        _switcher.RouteAV(input, 3);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(9)]
    public void RouteAV_IgnoresInvalidInputNumbers(int input)
    {
        _switcher.RouteAV(input, 0);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void RouteVideoAndRouteAudio_RecordAnEventAsTheyAreRefused()
    {
        _switcher.RouteVideo(2, 1);
        _switcher.RouteAudio(3, 1);

        Assert.Contains(_switcher.Events, e => e.Type == EventType.Input &&
            e.Info == "Not switching video output 1 to input 2 as this device doesn't support video breakaway");
        Assert.Contains(_switcher.Events, e => e.Type == EventType.Input &&
            e.Info == "Not switching audio output 1 to input 3 as this device doesn't support audio breakaway");
        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void ModelResponse_ShowsTheModel()
    {
        Assert.Equal(new DeviceDetail("Model", "SW4 HD 4K PLUS Series"), Detail("Model"));
    }

    [Fact]
    public void ModelResponse_ReadsTheIdentity()
    {
        Respond("Inf01*SW4 HD 4K PLUS Series");

        foreach (var query in IdentityQueries)
            _mockClient.Verify(client => client.Send(query), Times.Once);
    }

    [Fact]
    public async Task Heartbeat_DoesNotReadTheIdentityBeforeTheModelReply()
    {
        SetConnection(ConnectionState.Connected);
        _mockClient.Invocations.Clear();

        await Poll();

        foreach (var query in IdentityQueries)
            _mockClient.Verify(client => client.Send(query), Times.Never);
    }

    [Fact]
    public void IdentityReplies_AreShownAsReceived()
    {
        Respond("Ver01*1.01\r\nPno60-1604-01\r\nIpn SW4-HD-4K-PLUS-12-34-56\r\nIph 00-05-A6-12-34-56\r\n");

        Assert.Equal("1.01", Detail("Firmware")?.Value);
        Assert.Equal("60-1604-01", Detail("Part Number")?.Value);
        Assert.Equal("SW4-HD-4K-PLUS-12-34-56", Detail("Unit Name")?.Value);
        Assert.Equal("00-05-A6-12-34-56", Detail("MAC")?.Value);
        Assert.Empty(_switcher.GetOngoingIssues());
    }

    [Fact]
    public void RejectedIdentityQuery_ShowsItIsNotSupportedWithNoIssue()
    {
        Respond("Ver01*1.01");
        var communicationState = _switcher.CommunicationState;

        Respond("E10");

        Assert.Equal(new DeviceDetail("Part Number", "Not supported: E10", DetailTone.Normal), Detail("Part Number"));
        Assert.Empty(_switcher.GetIssues());
        Assert.Equal(communicationState, _switcher.CommunicationState);
    }

    [Fact]
    public async Task BusyIdentityQuery_IsAskedAgainAtTheNextPollAndRaisesTheReadIssueOnTheThirdInARow()
    {
        SetConnection(ConnectionState.Connected);
        foreach (var reply in ExtronBenchCaptures.Sw2ConnectReplies)
            Respond(reply);
        Respond(ExtronBenchCaptures.Sw2IdentityReplies[0]);
        Respond("E22");
        Respond(ExtronBenchCaptures.Sw2IdentityReplies[2]);
        Respond(ExtronBenchCaptures.Sw2IdentityReplies[3]);
        _mockClient.Invocations.Clear();
        await Poll();
        foreach (var reply in ExtronBenchCaptures.Sw2PollReplies)
            Respond(reply);
        Respond("E22");

        Assert.Empty(_switcher.GetIssues());
        _mockClient.Verify(client => client.Send("N"), Times.Once);

        await Poll();
        foreach (var reply in ExtronBenchCaptures.Sw2PollReplies)
            Respond(reply);
        Respond("E22");

        var issue = Assert.Single(_switcher.GetOngoingIssues());
        Assert.Equal("part-number-read", issue.Key);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Equal("The switcher did not answer N (Part Number) 3 times in a row, the last was E22", issue.Message);
        Respond(ExtronBenchCaptures.Sw2IdentityReplies[1]);
        Assert.Empty(_switcher.GetOngoingIssues());
    }

    [Fact]
    public async Task UnansweredIdentityQueries_AreAskedAgainAndRaiseTheReadIssueOnTheThirdMiss()
    {
        SetConnection(ConnectionState.Connected);
        Respond("Inf01*SW4 HD 4K PLUS Series");
        Respond("Ipn SW4-HD-4K-PLUS-12-34-56");
        _mockClient.Invocations.Clear();
        _time.Advance(QueryReplyTimeout);
        await Poll();
        _time.Advance(QueryReplyTimeout);
        await Poll();

        Assert.Empty(_switcher.GetIssues());
        foreach (var query in new[] { "Q", "N", "\u001bCH\r" })
            _mockClient.Verify(client => client.Send(query), Times.Exactly(2));
        _mockClient.Verify(client => client.Send("\u001bCN\r"), Times.Never);

        _time.Advance(QueryReplyTimeout);
        await Poll();

        Assert.Equal(["firmware-read", "part-number-read", "mac-read"], _switcher.GetOngoingIssues().Select(issue => issue.Key));
        Assert.Contains(_switcher.GetOngoingIssues(),
            issue => issue.Message == "The switcher did not answer Q (Firmware) 3 times in a row, the last was no Ver01* reply within 5 s");
        Assert.All(_switcher.GetOngoingIssues(), issue => Assert.Equal(IssueSeverity.Minor, issue.Severity));
    }

    [Fact]
    public async Task UnansweredIdentityQueries_WaitForTheReplyTimeout()
    {
        SetConnection(ConnectionState.Connected);
        Respond("Inf01*SW4 HD 4K PLUS Series");

        _time.Advance(QueryReplyTimeout - TimeSpan.FromTicks(1));
        await Poll();

        Assert.Empty(_switcher.GetOngoingIssues());
        foreach (var query in IdentityQueries)
            _mockClient.Verify(client => client.Send(query), Times.Once);
    }

    [Fact]
    public async Task LateIdentityReply_ResolvesTheIssue()
    {
        SetConnection(ConnectionState.Connected);
        Respond("Inf01*SW4 HD 4K PLUS Series");
        _time.Advance(QueryReplyTimeout);
        await Poll();
        _time.Advance(QueryReplyTimeout);
        await Poll();
        _time.Advance(QueryReplyTimeout);
        await Poll();
        Assert.Contains(_switcher.GetOngoingIssues(), issue => issue.Key == "firmware-read");

        Respond("Ver01*1.01");

        Assert.DoesNotContain(_switcher.GetOngoingIssues(), issue => issue.Key == "firmware-read");
        Assert.Equal("1.01", Detail("Firmware")?.Value);
    }

    [Fact]
    public void Disconnect_ForgetsTheOutstandingIdentityQueries()
    {
        SetConnection(ConnectionState.Connected);
        Respond("Inf01*SW4 HD 4K PLUS Series");
        SetConnection(ConnectionState.Disconnected);

        Respond("E10");

        Assert.Null(Detail("Firmware"));
        Assert.Empty(_switcher.GetOngoingIssues());
    }

    [Fact]
    public void RejectionWithNoOutstandingQuery_RaisesNoIssue()
    {
        var client = TestFactory.CreateCommunicationClient();
        using var switcher = new ExtronSw(client.Object, "Fresh switch", _time);

        client.Object.ResponseHandlers!("E13");

        Assert.Empty(switcher.GetIssues());
        Assert.Empty(switcher.Details);
    }

    [Fact]
    public void UnknownTaggedModel_IsShownAndRaisesAnIssue()
    {
        Respond("Inf01*SW9 HD 4K PLUS Series");

        Assert.Equal("SW9 HD 4K PLUS Series", Detail("Model")?.Value);
        var issue = Assert.Single(_switcher.GetOngoingIssues());
        Assert.Equal("model", issue.Key);
        Assert.Equal(IssueSeverity.Major, issue.Severity);
        Assert.Contains("Inf01*SW9 HD 4K PLUS Series", issue.Message);

        Respond("Inf01*SW4 HD 4K PLUS Series");

        Assert.Empty(_switcher.GetOngoingIssues());
        Assert.Equal("SW4 HD 4K PLUS Series", Detail("Model")?.Value);
    }
}

[Collection(LoggerFactoryCollection.Name)]
public class ExtronSwLogTest : IDisposable
{
    private readonly Mock<CommunicationClient> _client = TestFactory.CreateCommunicationClient();
    private readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly ExtronSw _switcher;

    public ExtronSwLogTest()
    {
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = _logs;
        try
        {
            _switcher = new ExtronSw(_client.Object, "Logged switch");
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }

    public void Dispose() => _switcher.Dispose();

    [Theory]
    [InlineData("Sig1 0 x 0*1", "Malformed signal status 1 0 x 0*1")]
    [InlineData("Sig1 0 1*1", "Malformed signal status 1 0 1*1")]
    [InlineData("Hdcp I1 2 3 1", "Malformed HDCP status I1 2 3 1")]
    [InlineData("Hdcp I1 2", "Ignoring the input HDCP status I1 2 as it doesn't list the 4 inputs")]
    [InlineData("Hdcp O1 2", "Ignoring the output HDCP status O1 2 as it doesn't list the one output")]
    [InlineData("In99999999999999999999 All", "Ignoring the tie In99999999999999999999 All as it names an input outside the 4 inputs")]
    [InlineData("In5 All", "Ignoring the tie In5 All as it names an input outside the 4 inputs")]
    public void InvalidResponses_AreLoggedWithTheRawValue(string response, string logged)
    {
        _client.Object.ResponseHandlers!("Inf01*SW4 HD 4K PLUS Series");

        _client.Object.ResponseHandlers!(response);

        Assert.Contains(_logs.Logs, log => log.Message == logged);
    }

    [Fact]
    public void TieBeforeTheInputCountIsKnown_IsLoggedAsSuch()
    {
        _client.Object.ResponseHandlers!("In2 All");

        Assert.Contains(_logs.Logs, log => log.Message == "Ignoring the tie In2 All as it arrived before the input count is known");
        Assert.Empty(_switcher.GetOutputs()[0].StreamAddress);
    }
}

[Collection(LoggerFactoryCollection.Name)]
public class ExtronSwBenchTest : IDisposable
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);
    private const string RouteTrigger = "5d30414a";
    private readonly FakeTimeProvider _time = new();
    private readonly Mock<SshClient> _client = TestFactory.CreateSshClient();
    private readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly ExtronSw _switcher;

    public ExtronSwBenchTest()
    {
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = _logs;
        try
        {
            _switcher = new ExtronSw(_client.Object, "Bench switch", _time);
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }

    public void Dispose() => _switcher.Dispose();

    private void Respond(params string[] replies)
    {
        foreach (var reply in replies)
            _client.Object.ResponseHandlers!.Invoke(reply);
    }

    private void Connect()
    {
        typeof(CommunicationClient).GetProperty(nameof(CommunicationClient.ConnectionState))!
            .SetValue(_client.Object, ConnectionState.Connected);
        Respond(ExtronBenchCaptures.Sw2ConnectReplies);
    }

    private Task Poll() => (Task)typeof(ExtronSw)
        .GetMethod("Poll", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(_switcher, [CancellationToken.None])!;

    private DeviceDetail? Detail(string label) => _switcher.Details.SingleOrDefault(detail => detail.Label == label);

    [Fact]
    public async Task BenchSequence_MatchesEveryReplyToTheCommandItAnswers()
    {
        Connect();
        Respond(ExtronBenchCaptures.Sw2IdentityReplies);
        _time.Advance(ReplyTimeout);

        await Poll();

        _client.Verify(client => client.Send(It.IsAny<string>()), Times.Exactly(14));
        Assert.Equal(["1.03", "60-1603-01", "ROOM-VID-SWI01", "00-05-A6-00-00-01"],
            new[] { "Firmware", "Part Number", "Unit Name", "MAC" }.Select(label => Detail(label)?.Value));
        Assert.DoesNotContain(_logs.Logs, log => log.Message.StartsWith("The switcher didn't answer"));
        Assert.DoesNotContain(_logs.Logs, log => log.Message.Contains("asking again"));
        Assert.DoesNotContain(_logs.Logs, log => log.Level >= LogLevel.Warning);
        Assert.Empty(_switcher.GetIssues());
    }

    [Fact]
    public async Task RejectedRoute_WhileAnIdentityQueryIsPending_IsReportedAndTheQueryKeepsItsReply()
    {
        Connect();
        Respond(ExtronBenchCaptures.Sw2IdentityReplies[0], ExtronBenchCaptures.Sw2IdentityReplies[1], ExtronBenchCaptures.Sw2IdentityReplies[3]);
        _time.Advance(ReplyTimeout);
        using (Trigger.Resume(RouteTrigger))
            _switcher.RouteAV(2, 1);
        await Poll();

        Respond("E13");
        Respond(ExtronBenchCaptures.Sw2PollReplies);
        Respond(ExtronBenchCaptures.Sw2IdentityReplies[2]);

        var issue = Assert.Single(_switcher.GetIssues());
        Assert.Equal(("rejected-command", IssueStatus.Momentary, "The switcher rejected 2! with E13"), (issue.Key, issue.Status, issue.Message));
        Assert.Equal(RouteTrigger, Assert.Single(_logs.Logs, log => log.Level == LogLevel.Warning).TriggerId);
        Assert.Equal(new DeviceDetail("Unit Name", "ROOM-VID-SWI01"), Detail("Unit Name"));
    }

    [Fact]
    public void FrontPanelTie_BetweenReplies_UpdatesTheTieAndTakesNoCommand()
    {
        Connect();
        Respond(ExtronBenchCaptures.Sw2IdentityReplies[0]);

        Respond("In2 All");

        Assert.Equal("2", _switcher.Outputs[0].StreamAddress);
        Respond("E10", ExtronBenchCaptures.Sw2IdentityReplies[2], ExtronBenchCaptures.Sw2IdentityReplies[3]);
        Assert.Equal(new DeviceDetail("Part Number", "Not supported: E10", DetailTone.Normal), Detail("Part Number"));
        Assert.Equal("ROOM-VID-SWI01", Detail("Unit Name")?.Value);
        Assert.DoesNotContain(_logs.Logs, log => log.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task TieReplyInTheInformationForm_AnswersTheTieQuery()
    {
        typeof(CommunicationClient).GetProperty(nameof(CommunicationClient.ConnectionState))!
            .SetValue(_client.Object, ConnectionState.Connected);
        Respond(ExtronBenchCaptures.Sw2ConnectReplies[..^1]);
        Respond("In1 Ausw0 Afmt0 Vmt0");
        Respond(ExtronBenchCaptures.Sw2IdentityReplies);
        _time.Advance(ReplyTimeout);

        await Poll();

        Assert.Equal("1", _switcher.Outputs[0].StreamAddress);
        Assert.DoesNotContain(_logs.Logs, log => log.Message.StartsWith("The switcher didn't answer"));
    }

    [Fact]
    public void ErrorWithNoCommandAwaitingAReply_IsLoggedAsAWarningWithTheRawText()
    {
        Respond("E13");

        var warning = Assert.Single(_logs.Logs, log => log.Level == LogLevel.Warning);
        Assert.Equal("The switcher sent E13 with no command awaiting a reply", warning.Message);
    }

    [Fact]
    public void CommandsBeyondTheTrackerLimit_DropTheOldestWithAWarning()
    {
        Connect();

        var routesToFillTheTracker = SentCommandTracker<int>.MaxCommands - ExtronBenchCaptures.Sw2IdentitySent.Length;
        for (var route = 0; route <= routesToFillTheTracker; route++)
            _switcher.RouteAV(2, 1);

        var warning = Assert.Single(_logs.Logs, log => log.Level == LogLevel.Warning);
        Assert.Equal("Dropped Q: more than 256 commands awaiting a reply", warning.Message);
    }
}
