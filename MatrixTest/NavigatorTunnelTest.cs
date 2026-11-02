using System.Net.Sockets;
using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

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

    private const string NotResponding = "Nav Encoder - Test at 1.1.1.1 is not responding via the Navigator tunnel";
    private readonly Mock<SshClient> _navigatorClient = TestFactory.CreateSshClient();

    private NavigatorTunnel TunnelThroughTheNavigator() => new("Nav Encoder - Test", "1.1.1.1", _navigatorClient.Object, null);

    private void SetNavigatorConnection(ConnectionState state) => typeof(CommunicationClient)
        .GetProperty(nameof(CommunicationClient.ConnectionState))!.SetValue(_navigatorClient.Object, state);

    private void FailNavigatorConnection() => typeof(CommunicationClient)
        .GetMethod("ReportConnectionFailure", BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(ConnectionReasonCategory), typeof(string), typeof(Exception)])!
        .Invoke(_navigatorClient.Object, [ConnectionReasonCategory.Refused, "host:22 refused the connection",
            new SocketException((int)SocketError.ConnectionRefused)]);

    [Fact]
    public void SetConnectionState_Disconnected_IsTimedOut()
    {
        _tunnel.SetConnectionState(ConnectionState.Disconnected);

        Assert.Equal(ConnectionReasonCategory.TimedOut, _tunnel.ConnectionReason?.Category);
        Assert.Equal(NotResponding, _tunnel.ConnectionReason?.Description);
    }

    [Fact]
    public void SetConnectionState_DisconnectedWhileTheNavigatorIsConnected_IsTimedOut()
    {
        SetNavigatorConnection(ConnectionState.Connected);
        var tunnel = TunnelThroughTheNavigator();

        tunnel.SetConnectionState(ConnectionState.Disconnected);

        Assert.Equal(ConnectionReasonCategory.TimedOut, tunnel.ConnectionReason?.Category);
        Assert.Equal(NotResponding, tunnel.ConnectionReason?.Description);
    }

    [Fact]
    public void WhileTheNavigatorIsDisconnected_TheTunnelMirrorsItsReason()
    {
        var tunnel = TunnelThroughTheNavigator();
        SetNavigatorConnection(ConnectionState.Disconnected);

        FailNavigatorConnection();
        Assert.Equal(_navigatorClient.Object.ConnectionReason, tunnel.ConnectionReason);
        Assert.Equal(ConnectionReasonCategory.Refused, tunnel.ConnectionReason?.Category);

        tunnel.SetConnectionState(ConnectionState.Disconnected);
        Assert.Equal(_navigatorClient.Object.ConnectionReason, tunnel.ConnectionReason);
        Assert.Equal(NotResponding, Assert.Single(tunnel.GetIssues()).Message);
    }

    [Fact]
    public void WhenTheNavigatorReconnects_AnUnansweredTunnelIsTimedOut()
    {
        var tunnel = TunnelThroughTheNavigator();
        SetNavigatorConnection(ConnectionState.Disconnected);
        FailNavigatorConnection();

        SetNavigatorConnection(ConnectionState.Connected);

        Assert.Equal(ConnectionReasonCategory.TimedOut, tunnel.ConnectionReason?.Category);
        Assert.Equal(NotResponding, tunnel.ConnectionReason?.Description);
        Assert.Empty(tunnel.GetIssues());
    }

    [Fact]
    public void AConnectedTunnel_DoesNotMirrorTheNavigator()
    {
        var tunnel = TunnelThroughTheNavigator();
        tunnel.SetConnectionState(ConnectionState.Connected);
        SetNavigatorConnection(ConnectionState.Disconnected);

        FailNavigatorConnection();

        Assert.Null(tunnel.ConnectionReason);
    }
}
