using System.Net.Sockets;

namespace AVCoders.Core.Tests;

public class ConnectionIssuesTest
{
    private class TestCommunicationClient() : CommunicationClient("Test", "10.0.0.1", 4999, CommandStringFormat.Ascii)
    {
        public override void Send(string message) { }

        public override void Send(byte[] bytes) { }

        public void ReportFailure(string reason) => ReportConnectionFailure(reason);

        public string Describe(Exception e) => DescribeConnectionError(e);

        public void SetConnectionState(ConnectionState state) => ConnectionState = state;
    }

    private readonly TestCommunicationClient _client = new();

    [Fact]
    public void ReportConnectionFailure_RaisesAMomentaryIssue()
    {
        _client.ReportFailure("The connection to 10.0.0.1:4999 timed out");

        var issue = Assert.Single(_client.Issues);
        Assert.Equal(CommunicationClient.ConnectionIssueKey, issue.Key);
        Assert.Equal(IssueStatus.Momentary, issue.Status);
        Assert.Equal("The connection to 10.0.0.1:4999 timed out", issue.Message);
        Assert.Empty(_client.OngoingIssues);
    }

    [Fact]
    public void ReportConnectionFailure_RepeatedFailures_Coalesce()
    {
        _client.ReportFailure("The connection to 10.0.0.1:4999 timed out");
        _client.ReportFailure("10.0.0.1:4999 refused the connection");

        var issue = Assert.Single(_client.Issues);
        Assert.Equal(2, issue.OccurrenceCount);
        Assert.Equal("10.0.0.1:4999 refused the connection", issue.Message);
    }

    [Fact]
    public void ReportConnectionFailure_BeforeTheThreshold_DoesNotRaiseAnOngoingIssue()
    {
        for (var i = 0; i < 10; i++)
            _client.ReportFailure("The connection to 10.0.0.1:4999 timed out");

        Assert.Empty(_client.OngoingIssues);
    }

    [Fact]
    public void ReportConnectionFailure_OnceTheThresholdHasPassed_RaisesACriticalOngoingIssue()
    {
        _client.ConnectionIssueThreshold = TimeSpan.Zero;

        _client.ReportFailure("The connection to 10.0.0.1:4999 timed out");

        var ongoing = Assert.Single(_client.OngoingIssues);
        Assert.Equal(CommunicationClient.ConnectionIssueKey, ongoing.Key);
        Assert.Equal(IssueSeverity.Critical, ongoing.Severity);
        Assert.StartsWith("Unable to connect since", ongoing.Message);
        Assert.Contains("The connection to 10.0.0.1:4999 timed out", ongoing.Message);
    }

    [Fact]
    public void Connecting_ResolvesTheOngoingIssue_AndResetsTheClock()
    {
        _client.ConnectionIssueThreshold = TimeSpan.Zero;
        _client.ReportFailure("The connection to 10.0.0.1:4999 timed out");
        var first = Assert.Single(_client.OngoingIssues);

        _client.SetConnectionState(ConnectionState.Connected);

        Assert.Empty(_client.OngoingIssues);
        Assert.Contains(_client.Issues,
            i => i.Key == CommunicationClient.ConnectionIssueKey && i.Status == IssueStatus.Resolved);

        _client.SetConnectionState(ConnectionState.Disconnected);
        _client.ReportFailure("10.0.0.1:4999 refused the connection");

        var second = Assert.Single(_client.OngoingIssues);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Connecting_WithoutAnyFailures_DoesNothing()
    {
        _client.SetConnectionState(ConnectionState.Connected);

        Assert.Empty(_client.Issues);
    }

    [Theory]
    [InlineData(SocketError.TimedOut, "The connection to 10.0.0.1:4999 timed out")]
    [InlineData(SocketError.HostNotFound, "The host 10.0.0.1 was not found")]
    [InlineData(SocketError.ConnectionRefused, "10.0.0.1:4999 refused the connection")]
    [InlineData(SocketError.HostUnreachable, "The host 10.0.0.1 is unreachable")]
    [InlineData(SocketError.NetworkUnreachable, "The network is unreachable trying to reach 10.0.0.1")]
    [InlineData(SocketError.ConnectionReset, "The connection to 10.0.0.1:4999 was reset")]
    public void DescribeConnectionError_MapsSocketErrorsToHumanReadableText(SocketError error, string expected)
    {
        Assert.Equal(expected, _client.Describe(new SocketException((int)error)));
    }

    [Fact]
    public void DescribeConnectionError_MapsACancelledAttemptToATimeout()
    {
        Assert.Equal("The connection attempt to 10.0.0.1:4999 timed out",
            _client.Describe(new OperationCanceledException()));
    }

    [Fact]
    public void DescribeConnectionError_UnwrapsInnerSocketExceptions()
    {
        var wrapped = new HttpRequestException("boom",
            new SocketException((int)SocketError.ConnectionRefused));

        Assert.Equal("10.0.0.1:4999 refused the connection", _client.Describe(wrapped));
    }

    [Fact]
    public void DescribeConnectionError_FallsBackToTheExceptionMessage()
    {
        Assert.Equal("The connection to 10.0.0.1:4999 failed: no route",
            _client.Describe(new InvalidOperationException("no route")));
    }
}
