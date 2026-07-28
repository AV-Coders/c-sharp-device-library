using AVCoders.Core;

namespace AVCoders.Matrix.Tests;

public class NavigatorTunnelTest
{
    private readonly NavigatorTunnel _tunnel = new("Nav Encoder - Test", "1.1.1.1");

    [Fact]
    public void SetConnectionState_Disconnected_RaisesAMomentaryIssue()
    {
        _tunnel.SetConnectionState(ConnectionState.Disconnected);

        var issue = Assert.Single(_tunnel.GetIssues());
        Assert.Equal(CommunicationClient.ConnectionIssueKey, issue.Key);
        Assert.Equal(IssueStatus.Momentary, issue.Status);
        Assert.Equal("Nav Encoder - Test at 1.1.1.1 is not responding via the Navigator tunnel", issue.Message);
        Assert.Empty(_tunnel.GetOngoingIssues());
    }

    [Fact]
    public void SetConnectionState_RepeatedDisconnected_CoalescesTheMomentaryIssue()
    {
        _tunnel.SetConnectionState(ConnectionState.Disconnected);
        _tunnel.SetConnectionState(ConnectionState.Disconnected);
        _tunnel.SetConnectionState(ConnectionState.Disconnected);

        var issue = Assert.Single(_tunnel.GetIssues());
        Assert.Equal(3, issue.OccurrenceCount);
    }

    [Fact]
    public void SetConnectionState_OnceTheThresholdHasPassed_RaisesACriticalOngoingIssue()
    {
        _tunnel.ConnectionIssueThreshold = TimeSpan.Zero;

        _tunnel.SetConnectionState(ConnectionState.Disconnected);

        var ongoing = Assert.Single(_tunnel.GetOngoingIssues());
        Assert.Equal(IssueSeverity.Critical, ongoing.Severity);
        Assert.StartsWith("Unable to connect since", ongoing.Message);
    }

    [Fact]
    public void SetConnectionState_Connected_ResolvesTheOngoingIssue()
    {
        _tunnel.ConnectionIssueThreshold = TimeSpan.Zero;
        _tunnel.SetConnectionState(ConnectionState.Disconnected);

        _tunnel.SetConnectionState(ConnectionState.Connected);

        Assert.Empty(_tunnel.GetOngoingIssues());
        Assert.Contains(_tunnel.GetIssues(),
            i => i.Key == CommunicationClient.ConnectionIssueKey && i.Status == IssueStatus.Resolved);
    }

    [Fact]
    public void SetConnectionState_Error_RaisesAMomentaryIssue()
    {
        _tunnel.SetConnectionState(ConnectionState.Error);

        Assert.Single(_tunnel.GetIssues());
    }

    [Fact]
    public void SetConnectionState_Connecting_RaisesNothing()
    {
        _tunnel.SetConnectionState(ConnectionState.Connecting);

        Assert.Empty(_tunnel.GetIssues());
    }
}
