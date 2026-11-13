using AVCoders.Core;
using Microsoft.Extensions.Time.Testing;

namespace AvCoders.Interface.Tests;

public class GlobalCacheContactClosureTest
{
    // Invented: no capture from a unit exists. Shaped on the iTach API v1.5 (5.4.1 state, 6 error codes, 5.1 unknowncommand); check against a device.
    private const string Relay1Closed = "state,1:1,1\r";
    private const string Relay1Open = "state,1:1,0\r";
    private const string Relay2Open = "state,1:2,0\r";
    private const string BadConnector = "ERR_1:4,003\r";
    private const string UnknownCommand = "unknowncommand, 001\r";
    private const string TelnetNegotiation = "ÿý\u0018ÿý\u001F";

    private readonly FakeTimeProvider _time = new();
    private readonly RecordingClient _client = new();
    private readonly GlobalCacheContactClosure _closure;

    public GlobalCacheContactClosureTest()
    {
        _closure = new GlobalCacheContactClosure(_client, "Closure", GlobalCacheContactClosure.DefaultModule,
            GlobalCacheContactClosure.Ip2ccRelayCount, _time);
    }

    private string? Detail(string label) => _closure.Details.SingleOrDefault(detail => detail.Label == label)?.Value;

    private Issue? IssueFor(string key) => _closure.GetIssues().SingleOrDefault(issue => issue.Key == key);

    [Fact]
    public void Close_SendsSetStateOne_ToTheRelay()
    {
        _closure.Close(1);

        Assert.Equal(["setstate,1:1,1\r"], _client.Sent);
    }

    [Fact]
    public void Open_SendsSetStateZero_ToTheRelay()
    {
        _closure.Open(2);

        Assert.Equal(["setstate,1:2,0\r"], _client.Sent);
    }

    [Fact]
    public void AnotherModule_IsAddressedInEveryCommand()
    {
        var client = new RecordingClient();
        var closure = new GlobalCacheContactClosure(client, "GC-100 relays", 3, 1);

        closure.Close(1);
        client.SetState(ConnectionState.Connected);

        Assert.Equal(["setstate,3:1,1\r", "getstate,3:1\r", "setstate,3:1,1\r"], client.Sent);
    }

    [Fact]
    public void Close_WhileTheClientIsNotConnected_IsHandedToTheClient()
    {
        _client.SetState(ConnectionState.Disconnected);

        _closure.Close(1);

        Assert.Equal(["setstate,1:1,1\r"], _client.Sent);
    }

    [Fact]
    public void Connecting_AsksForEveryRelaysState()
    {
        _client.SetState(ConnectionState.Connected);

        Assert.Equal(["getstate,1:1\r", "getstate,1:2\r", "getstate,1:3\r"], _client.Sent);
    }

    [Fact]
    public void Connecting_SetsEachRelayToTheLastStateTheProgramCommanded_AfterAskingTheirStates()
    {
        _closure.Close(1);
        _closure.Open(2);
        _client.Sent.Clear();

        _client.SetState(ConnectionState.Connected);

        Assert.Equal(["getstate,1:1\r", "getstate,1:2\r", "getstate,1:3\r", "setstate,1:1,1\r", "setstate,1:2,0\r"], _client.Sent);
    }

    [Fact]
    public void ACloseMadeWhileDisconnected_IsSentAgainOnTheNextConnect()
    {
        _client.SetState(ConnectionState.Disconnected);
        _closure.Close(1);

        _client.SetState(ConnectionState.Connected);

        Assert.Equal(["setstate,1:1,1\r", "getstate,1:1\r", "getstate,1:2\r", "getstate,1:3\r", "setstate,1:1,1\r"], _client.Sent);
    }

    [Fact]
    public void Reconnecting_SetsTheLastCommandForARelay()
    {
        _closure.Close(1);
        _closure.Open(1);
        _client.SetState(ConnectionState.Connected);
        _client.SetState(ConnectionState.Disconnected);
        _client.Sent.Clear();

        _client.SetState(ConnectionState.Connected);

        Assert.Equal("setstate,1:1,0\r", _client.Sent.Last());
        Assert.Single(_client.Sent, sent => sent.StartsWith("setstate"));
    }

    [Fact]
    public void StateReplies_ShowEachRelay()
    {
        _client.Receive(Relay1Closed + Relay2Open);

        Assert.Equal("Closed", Detail("Relay 1:1"));
        Assert.Equal("Open", Detail("Relay 1:2"));
        Assert.Equal(CommunicationState.Okay, _closure.CommunicationState);
    }

    [Fact]
    public void AReplySplitAcrossReads_IsJoined()
    {
        _client.Receive(Relay1Closed[..7]);
        _client.Receive(Relay1Closed[7..]);

        Assert.Equal("Closed", Detail("Relay 1:1"));
    }

    [Fact]
    public void ALaterReply_ReplacesTheRelaysState()
    {
        _client.Receive(Relay1Closed);
        _client.Receive(Relay1Open);

        Assert.Equal("Open", Detail("Relay 1:1"));
    }

    [Fact]
    public void AnUnknownStateValue_IsShownAsReceived()
    {
        _client.Receive("state,1:1,7\r");

        Assert.Equal("7", Detail("Relay 1:1"));
    }

    [Fact]
    public void AStateReplyWithNoCommaBeforeTheValue_RaisesAnUnreadableReplyIssueShowingIt()
    {
        _client.Receive("state,1:11\r");

        var issue = IssueFor(GlobalCacheContactClosure.UnreadableReplyIssueKey);
        Assert.Equal("The Global Cache sent a reply this driver can't read: state,1:11", issue?.Message);
        Assert.Empty(_closure.Details);
    }

    [Fact]
    public void AnUnrecognisedLine_RaisesAnUnreadableReplyIssueShowingIt()
    {
        _client.Receive("HTTP/1.1 400 Bad Request\r\n");

        var issue = IssueFor(GlobalCacheContactClosure.UnreadableReplyIssueKey);
        Assert.Equal("The Global Cache sent a reply this driver can't read: HTTP/1.1 400 Bad Request", issue?.Message);
    }

    [Fact]
    public void TextWithNoLineEndPastTheCap_RaisesAnIssueShowingItEscaped()
    {
        _client.Receive("\u0001" + new string('x', GlobalCacheContactClosure.MaxUnterminatedLength));

        var issue = IssueFor(GlobalCacheContactClosure.UnreadableReplyIssueKey);
        Assert.Equal((IssueStatus.Momentary, IssueSeverity.Minor), (issue?.Status, issue?.Severity));
        Assert.Equal($"Dropped {GlobalCacheContactClosure.MaxUnterminatedLength + 1} characters from the Global Cache with no line end: \\x01{new string('x', 199)}...",
            issue?.Message);
    }

    [Fact]
    public void TextWithNoLineEndPastTheCap_IsDropped_SoTheNextReplyIsRead()
    {
        _client.Receive(new string('x', GlobalCacheContactClosure.MaxUnterminatedLength + 1));

        _client.Receive(Relay1Closed);

        Assert.Equal("Closed", Detail("Relay 1:1"));
    }

    [Fact]
    public void ACommandLeftUnanswered_IsReportedAReplyTimeoutAfterItWasWritten_WithNothingElseSent()
    {
        _closure.Close(1);
        _time.Advance(GlobalCacheContactClosure.ReplyTimeout - TimeSpan.FromTicks(1));
        Assert.Null(IssueFor(GlobalCacheContactClosure.UnansweredIssueKey));

        _time.Advance(TimeSpan.FromTicks(1));

        var issue = IssueFor(GlobalCacheContactClosure.UnansweredIssueKey);
        Assert.Equal((IssueStatus.Momentary, IssueSeverity.Minor), (issue?.Status, issue?.Severity));
        Assert.Equal("The Global Cache did not answer setstate,1:1,1 within 5 s", issue?.Message);
    }

    [Fact]
    public void TwoCommandsLeftUnanswered_AreEachReportedAReplyTimeoutAfterTheirOwnWrite()
    {
        _closure.Close(1);
        _time.Advance(TimeSpan.FromSeconds(3));
        _closure.Open(2);

        _time.Advance(TimeSpan.FromSeconds(2));
        var first = IssueFor(GlobalCacheContactClosure.UnansweredIssueKey);
        _time.Advance(TimeSpan.FromSeconds(3));
        var second = IssueFor(GlobalCacheContactClosure.UnansweredIssueKey);

        Assert.Equal(("The Global Cache did not answer setstate,1:1,1 within 5 s", 1), (first?.Message, first?.OccurrenceCount));
        Assert.Equal(("The Global Cache did not answer setstate,1:2,0 within 5 s", 2), (second?.Message, second?.OccurrenceCount));
    }

    [Fact]
    public void ACommandLeftUnanswered_RaisesAnIssueShowingTheTextWithNoLineEnd()
    {
        _closure.Close(1);
        _client.Receive(TelnetNegotiation);

        _time.Advance(GlobalCacheContactClosure.ReplyTimeout);

        Assert.Equal(@"The Global Cache did not answer setstate,1:1,1 within 5 s (it sent 6 characters with no line end: \xFF\xFD\x18\xFF\xFD\x1F)",
            IssueFor(GlobalCacheContactClosure.UnansweredIssueKey)?.Message);
    }

    [Fact]
    public void AStateReply_AnswersTheCommandForItsRelay_SoItIsNotReportedUnanswered()
    {
        _closure.Close(1);
        _client.Receive(Relay1Closed);

        _time.Advance(GlobalCacheContactClosure.ReplyTimeout);

        Assert.Null(IssueFor(GlobalCacheContactClosure.UnansweredIssueKey));
    }

    [Fact]
    public void AStateReplyForAnotherRelay_LeavesTheCommandUnanswered()
    {
        _closure.Close(1);
        _client.Receive(Relay2Open);

        _time.Advance(GlobalCacheContactClosure.ReplyTimeout);

        Assert.StartsWith("The Global Cache did not answer setstate,1:1,1 within 5 s", IssueFor(GlobalCacheContactClosure.UnansweredIssueKey)?.Message);
    }

    [Fact]
    public void AStateReply_IsRecordedUnderTheTriggerIdOfTheCommandItAnswers()
    {
        var press = Trigger.Start();
        _closure.Close(1);
        Trigger.Start();

        _client.Receive(Relay1Closed);

        var okay = Assert.Single(_closure.Events, recorded => recorded.Type == EventType.DriverState && recorded.Info == nameof(CommunicationState.Okay));
        Assert.Equal(press, okay.Context[LogBase.TriggerIdProperty]);
    }

    [Fact]
    public void ARejection_RaisesAMinorMomentaryIssue_NamingTheCommandAndTheRawReply()
    {
        _closure.Close(4);

        _client.Receive(BadConnector);

        var issue = IssueFor(GlobalCacheContactClosure.RejectedIssueKey);
        Assert.Equal((IssueStatus.Momentary, IssueSeverity.Minor), (issue?.Status, issue?.Severity));
        Assert.Equal("The Global Cache rejected setstate,1:4,1 with ERR_1:4,003", issue?.Message);
    }

    [Fact]
    public void AnUnknownCommandReply_IsARejection_NamingTheCommand()
    {
        _closure.Open(1);

        _client.Receive(UnknownCommand);

        Assert.Equal("The Global Cache rejected setstate,1:1,0 with unknowncommand, 001", IssueFor(GlobalCacheContactClosure.RejectedIssueKey)?.Message);
    }

    [Fact]
    public void ARejection_IsRecordedUnderTheTriggerIdOfThePressThatMadeIt()
    {
        var press = Trigger.Start();
        _closure.Close(4);
        Trigger.Start();

        _client.Receive(BadConnector);

        var rejection = Assert.Single(_closure.Events, recorded => recorded.Info.StartsWith("The Global Cache rejected"));
        Assert.Equal(press, rejection.Context[LogBase.TriggerIdProperty]);
    }

    [Fact]
    public void ARejectionWithNoCommandAwaitingIt_RaisesNoIssue()
    {
        _client.Receive(BadConnector);

        Assert.Null(IssueFor(GlobalCacheContactClosure.RejectedIssueKey));
    }

    [Fact]
    public void Disconnecting_MakesTheCommunicationStateUnknown()
    {
        _client.Receive(Relay1Closed);
        _client.SetState(ConnectionState.Connected);

        _client.SetState(ConnectionState.Disconnected);

        Assert.Equal(CommunicationState.Unknown, _closure.CommunicationState);
    }

    [Fact]
    public void ADisconnect_ForgetsTheCommandsAwaitingAReply()
    {
        _closure.Close(1);
        _client.SetState(ConnectionState.Disconnected);

        _time.Advance(GlobalCacheContactClosure.ReplyTimeout);

        Assert.Null(IssueFor(GlobalCacheContactClosure.UnansweredIssueKey));
    }

    [Fact]
    public void ADisconnect_DropsAPartReply_SoTheFirstReplyAfterTheReconnectIsRead()
    {
        _client.Receive("state,1:");
        _client.SetState(ConnectionState.Disconnected);

        _client.Receive(Relay1Closed);

        Assert.Equal("Closed", Detail("Relay 1:1"));
    }

    private sealed class RecordingClient() : CommunicationClient("Closure", "host", GlobalCacheContactClosure.DefaultPort, CommandStringFormat.Ascii)
    {
        public readonly List<string> Sent = [];

        public override void Send(string message)
        {
            Sent.Add(message);
            InvokeRequestHandlers(message);
        }

        public override void Send(byte[] bytes) => throw new NotSupportedException();

        public void Receive(string response) => InvokeResponseHandlers(response);

        public void SetState(ConnectionState state) => ConnectionState = state;
    }
}
