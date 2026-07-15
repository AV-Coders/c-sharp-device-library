using System.Net;
using System.Net.Sockets;

namespace AVCoders.CommunicationClients.Tests;

/// <summary>
/// Reconnection and send-queue behaviour. These tests exercise the connection-state
/// worker's real backoff/poll cycles, so their timeouts are generous by design.
/// </summary>
public class AvCodersTcpClientResilienceTest : IDisposable
{
    private AvCodersTcpClient? _client;

    public void Dispose()
    {
        try { _client?.Disconnect(); } catch { /* test teardown */ }
    }

    [Fact]
    public async Task Send_WhileDisconnected_QueuesAndDeliversAfterConnect()
    {
        var port = TestNetwork.GetFreePort();
        _client = new AvCodersTcpClient("127.0.0.1", port, "QueueTestClient", CommandStringFormat.Ascii);
        _client.SetQueueTimeout(60);

        // Nothing is listening yet - this must be queued, not lost.
        _client.Send("queued message");

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            using var serverSide = await TestNetwork.AcceptAsync(listener, 30);
            var received = await TestNetwork.ReadStringAsync(serverSide, 30);
            Assert.Equal("queued message", received);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Client_Reconnects_AfterServerDropsTheConnection()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        _client = new AvCodersTcpClient("127.0.0.1", port, "ReconnectTestClient", CommandStringFormat.Ascii);

        try
        {
            var firstConnection = await TestNetwork.AcceptAsync(listener, 15);
            await TestNetwork.WaitUntilAsync(
                () => _client.ConnectionState == ConnectionState.Connected,
                15, "client never connected the first time");

            firstConnection.Close();

            // The connection-state worker polls every 15s while connected, so a
            // dropped link can take up to ~20s to re-establish.
            using var secondConnection = await TestNetwork.AcceptAsync(listener, 30);
            await TestNetwork.WaitUntilAsync(
                () => _client.ConnectionState == ConnectionState.Connected,
                15, "client never reported Connected after reconnecting");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task FailedConnection_ReportsDisconnected_NotError()
    {
        // No listener on this port - connection is refused immediately.
        _client = new AvCodersTcpClient("127.0.0.1", TestNetwork.GetFreePort(), "RefusedTestClient",
            CommandStringFormat.Ascii);

        await TestNetwork.WaitUntilAsync(
            () => _client.ConnectionState == ConnectionState.Disconnected,
            15, "client never reported Disconnected after a refused connection");
    }
}
