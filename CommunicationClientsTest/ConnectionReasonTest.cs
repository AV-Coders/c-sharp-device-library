using System.Net.Sockets;

namespace AVCoders.CommunicationClients.Tests;

public class ConnectionReasonTest
{
    private sealed class ReasonClient(TimeProvider timeProvider)
        : CommunicationClient("Test", "10.0.0.1", 4999, CommandStringFormat.Ascii, timeProvider)
    {
        public override void Send(string message) { }

        public override void Send(byte[] bytes) { }

        public void Fail(Exception e) => ReportConnectionFailure(e);

        public void Fail(string reason) => ReportConnectionFailure(reason);

        public void SetState(ConnectionState state) => ConnectionState = state;

        public void Connect() => DesiredConnectionState = ConnectionState.Connected;

        public void Disconnect()
        {
            DesiredConnectionState = ConnectionState.Disconnected;
            ClearConnectionFailures();
            ConnectionState = ConnectionState.Disconnected;
        }

        public void ClearFailures() => ClearConnectionFailures();
    }

    private static readonly TimeSpan Later = TimeSpan.FromSeconds(30);
    private readonly FakeTimeProvider _time = new();
    private readonly ReasonClient _client;
    private readonly List<ConnectionReason?> _changes = [];

    public ConnectionReasonTest()
    {
        _client = new ReasonClient(_time);
        _client.Connect();
        _client.ConnectionReasonHandlers += _changes.Add;
    }

    [Theory]
    [InlineData(SocketError.TimedOut, ConnectionReasonCategory.TimedOut, "The connection to 10.0.0.1:4999 timed out")]
    [InlineData(SocketError.ConnectionRefused, ConnectionReasonCategory.Refused, "10.0.0.1:4999 refused the connection")]
    [InlineData(SocketError.HostNotFound, ConnectionReasonCategory.HostNotFound, "The host 10.0.0.1 was not found")]
    [InlineData(SocketError.HostUnreachable, ConnectionReasonCategory.Unreachable, "The host 10.0.0.1 is unreachable")]
    [InlineData(SocketError.NetworkUnreachable, ConnectionReasonCategory.Unreachable, "The network is unreachable trying to reach 10.0.0.1")]
    [InlineData(SocketError.ConnectionReset, ConnectionReasonCategory.ClosedByDevice, "The connection to 10.0.0.1:4999 was reset")]
    public void AFailedAttempt_SetsTheCategoryAndTheDescriptionOfItsSocketError(SocketError error, ConnectionReasonCategory category, string description)
    {
        var exception = new SocketException((int)error);

        _client.Fail(exception);

        var reason = Assert.IsType<ConnectionReason>(_client.ConnectionReason);
        Assert.Equal(category, reason.Category);
        Assert.Equal(description, reason.Description);
        Assert.Equal($"System.Net.Sockets.SocketException: {exception.Message}", reason.RawException);
        Assert.Equal(_time.GetUtcNow(), reason.Since);
    }

    [Fact]
    public void TheClientsOwnConnectTimeout_IsTimedOut()
    {
        _client.Fail(new OperationCanceledException());

        Assert.Equal(ConnectionReasonCategory.TimedOut, _client.ConnectionReason!.Category);
        Assert.Equal("The connection attempt to 10.0.0.1:4999 timed out", _client.ConnectionReason.Description);
    }

    [Fact]
    public void AWrappedSocketError_TakesTheCategoryOfTheSocketError_AndKeepsTheWholeExceptionText()
    {
        _client.Fail(new HttpRequestException("No connection could be made (10.0.0.1:4999)",
            new SocketException((int)SocketError.ConnectionRefused)));

        var reason = _client.ConnectionReason!;
        Assert.Equal(ConnectionReasonCategory.Refused, reason.Category);
        Assert.StartsWith("System.Net.Http.HttpRequestException: No connection could be made (10.0.0.1:4999) ---> System.Net.Sockets.SocketException: ",
            reason.RawException);
    }

    [Fact]
    public void AnyOtherFailure_IsOther_WithItsRawText()
    {
        _client.Fail(new InvalidOperationException("garbage \u0001 reply"));

        var reason = _client.ConnectionReason!;
        Assert.Equal(ConnectionReasonCategory.Other, reason.Category);
        Assert.Equal("The connection to 10.0.0.1:4999 failed: garbage \u0001 reply", reason.Description);
        Assert.Equal("System.InvalidOperationException: garbage \u0001 reply", reason.RawException);
    }

    [Fact]
    public void AFailureReportedAsText_IsOther_WithNoException()
    {
        _client.Fail("10.0.0.1 is not responding via the Navigator tunnel");

        Assert.Equal(new ConnectionReason(ConnectionReasonCategory.Other, "10.0.0.1 is not responding via the Navigator tunnel",
            null, _time.GetUtcNow()), _client.ConnectionReason);
    }

    [Fact]
    public void TheSameFailureAgain_KeepsItsSince_AndRaisesNoChange()
    {
        _client.Fail(new SocketException((int)SocketError.ConnectionRefused));
        var firstSeen = _time.GetUtcNow();
        _time.Advance(Later);

        _client.Fail(new SocketException((int)SocketError.ConnectionRefused));

        Assert.Equal(firstSeen, _client.ConnectionReason!.Since);
        Assert.Single(_changes);
    }

    [Fact]
    public void ADifferentCategory_ReplacesTheReason_WithANewSince()
    {
        _client.Fail(new SocketException((int)SocketError.ConnectionRefused));
        _time.Advance(Later);

        _client.Fail(new SocketException((int)SocketError.TimedOut));

        Assert.Equal(ConnectionReasonCategory.TimedOut, _client.ConnectionReason!.Category);
        Assert.Equal(_time.GetUtcNow(), _client.ConnectionReason.Since);
        Assert.Equal(2, _changes.Count);
    }

    [Fact]
    public void Connected_ClearsTheReason()
    {
        _client.Fail(new SocketException((int)SocketError.ConnectionRefused));

        _client.SetState(ConnectionState.Connected);

        Assert.Null(_client.ConnectionReason);
        Assert.Null(_changes[^1]);
    }

    [Fact]
    public void ConnectionState_IsNotChangedByAFailure()
    {
        var states = new List<ConnectionState>();
        _client.ConnectionStateHandlers += states.Add;

        _client.Fail(new SocketException((int)SocketError.ConnectionRefused));

        Assert.Empty(states);
        Assert.Equal(ConnectionState.Unknown, _client.ConnectionState);
    }

    [Fact]
    public void ARequestedDisconnect_IsRequested_EvenAfterAFailure()
    {
        _client.Fail(new SocketException((int)SocketError.ConnectionRefused));
        _time.Advance(Later);

        _client.Disconnect();

        Assert.Equal(new ConnectionReason(ConnectionReasonCategory.Requested, "Disconnected on request", null, _time.GetUtcNow()),
            _client.ConnectionReason);
    }

    [Fact]
    public void AFailureWhileDisconnectedOnRequest_LeavesItRequested()
    {
        _client.Disconnect();

        _client.Fail(new SocketException((int)SocketError.ConnectionRefused));
        _client.ClearFailures();

        Assert.Equal(ConnectionReasonCategory.Requested, _client.ConnectionReason!.Category);
    }

    [Fact]
    public void Connect_AfterARequestedDisconnect_ClearsTheReason()
    {
        _client.Disconnect();

        _client.Connect();

        Assert.Null(_client.ConnectionReason);
    }

    [Fact]
    public void Connect_WhileFailing_KeepsTheFailure()
    {
        _client.Fail(new SocketException((int)SocketError.ConnectionRefused));

        _client.Connect();

        Assert.Equal(ConnectionReasonCategory.Refused, _client.ConnectionReason!.Category);
    }

    [Fact]
    public void AHandlerThatThrows_IsLogged_AndTheReasonIsStillSet()
    {
        _client.ConnectionReasonHandlers += _ => throw new InvalidOperationException("subscriber failed");

        _client.Fail(new SocketException((int)SocketError.ConnectionRefused));

        Assert.Equal(ConnectionReasonCategory.Refused, _client.ConnectionReason!.Category);
        Assert.Contains(_client.Errors, error => error.Exception is InvalidOperationException);
    }

    [Fact]
    public void TheConnectionIssueText_IsUnchanged()
    {
        _client.Fail(new SocketException((int)SocketError.ConnectionRefused));

        Assert.Equal("10.0.0.1:4999 refused the connection", Assert.Single(_client.GetIssues()).Message);
    }
}
