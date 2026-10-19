using System.Collections.Concurrent;

namespace AVCoders.CommunicationClients.Tests;

public class AvCodersSshClientTest : IDisposable
{
    private readonly AvCodersSshClient _client =
        new("127.0.0.1", TestNetwork.GetFreePort(), "user", "password", "TestSshClient");

    public void Dispose()
    {
        try { _client.Disconnect(); } catch { /* test teardown */ }
    }

    private async Task DisconnectAfterTheFirstFailedAttempt()
    {
        await TestNetwork.WaitUntilAsync(
            () => _client.ConnectionState == ConnectionState.Disconnected && _client.GetIssues().Any(),
            30, "the refused connection was never reported");
        _client.Disconnect();
    }

    [Fact]
    public async Task Disconnect_StopsAllWorkers()
    {
        await DisconnectAfterTheFirstFailedAttempt();

        Assert.All(TestNetwork.Workers(_client), w => Assert.False(w.IsRunning));
        Assert.Equal(ConnectionState.Disconnected, _client.DesiredConnectionState);
    }

    [Fact]
    public async Task Send_WhileDisconnectedOnRequest_IsDroppedWithoutRaisingAnError()
    {
        await DisconnectAfterTheFirstFailedAttempt();
        var requests = 0;
        _client.RequestHandlers += _ => requests++;
        _client.RequestByteHandlers += _ => requests++;

        _client.Send("dropped");
        _client.Send("dropped"u8.ToArray());

        Assert.Equal(0, requests);
        Assert.Equal(0, TestNetwork.SendQueueCount(_client));
        Assert.Equal(ConnectionState.Disconnected, _client.ConnectionState);
    }

    [Fact]
    public async Task Reconnect_WhileDisconnectedOnRequest_DoesNothing()
    {
        await DisconnectAfterTheFirstFailedAttempt();
        var states = new ConcurrentQueue<ConnectionState>();
        _client.ConnectionStateHandlers += states.Enqueue;

        _client.Reconnect();
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.Empty(states);
    }

    [Fact]
    public async Task Connect_AfterDisconnect_TriesToConnectAgain()
    {
        await DisconnectAfterTheFirstFailedAttempt();
        var states = new ConcurrentQueue<ConnectionState>();
        _client.ConnectionStateHandlers += states.Enqueue;

        _client.Connect();

        await TestNetwork.WaitUntilAsync(() => states.Contains(ConnectionState.Connecting),
            15, "the client never tried to connect after Connect");
        Assert.Equal(ConnectionState.Connected, _client.DesiredConnectionState);
    }
}
