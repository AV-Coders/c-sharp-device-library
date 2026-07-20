using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AVCoders.Core;

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

    // A genuine zero-window wedge is not reproducible on Windows loopback (window
    // autotuning ignores SO_RCVBUF), so these two tests force the same code paths
    // deterministically: a write that cannot proceed within WriteTimeout, and a write the
    // socket rejects outright.

    [Fact]
    public async Task Send_WhenTheWriteCannotProceed_TimesOutAndDeliversThePayloadOnce()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        _client = new AvCodersTcpClient("127.0.0.1", port, "TimeoutTestClient", CommandStringFormat.Ascii);
        _client.SetQueueTimeout(60);

        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, 15);
            await TestNetwork.WaitUntilAsync(
                () => _client.ConnectionState == ConnectionState.Connected,
                15, "client never connected");

            // Holding the write lock stands in for a wedged WriteAsync: Send must give up
            // within WriteTimeout instead of blocking the caller indefinitely.
            var writeLock = GetWriteLock(_client);
            await writeLock.WaitAsync();
            var payload = "timeout-marker"u8.ToArray();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                _client.Send(payload);
            }
            finally
            {
                stopwatch.Stop();
                writeLock.Release();
            }
            Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(3),
                $"Send returned in {stopwatch.Elapsed} without waiting for the write timeout");
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
                $"Send blocked the caller for {stopwatch.Elapsed} instead of timing out");

            firstConnection.Close();

            // The client reconnects and drains the queue - the payload must arrive exactly
            // once, not twice (the old Send enqueued a failed payload in the catch AND on
            // the fall-through path).
            using var second = await TestNetwork.AcceptAsync(listener, 30);
            var totalReceived = await ReadUntilQuietAsync(second, payload.Length);
            Assert.Equal(payload.Length, totalReceived);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Send_WhenTheSocketRejectsTheWrite_DeliversThePayloadOnceAfterReconnect()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        _client = new AvCodersTcpClient("127.0.0.1", port, "RejectTestClient", CommandStringFormat.Ascii);
        _client.SetQueueTimeout(60);

        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, 15);
            await TestNetwork.WaitUntilAsync(
                () => _client.ConnectionState == ConnectionState.Connected,
                15, "client never connected");

            // After a send-side shutdown the socket still reports connected, so Send takes
            // the write path and the write itself throws.
            var innerField = typeof(AvCodersTcpClient).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var inner = (System.Net.Sockets.TcpClient)innerField.GetValue(_client)!;
            inner.Client.Shutdown(SocketShutdown.Send);

            var payload = "reject-marker"u8.ToArray();
            _client.Send(payload);

            firstConnection.Close();

            using var second = await TestNetwork.AcceptAsync(listener, 30);
            var totalReceived = await ReadUntilQuietAsync(second, payload.Length);
            Assert.Equal(payload.Length, totalReceived);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static SemaphoreSlim GetWriteLock(AvCodersTcpClient client)
    {
        var field = typeof(AvCodersTcpClient).GetField("_writeLock", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (SemaphoreSlim)field.GetValue(client)!;
    }

    [Fact]
    public async Task Disconnect_StopsAllWorkers_AndConnectRestartsThem()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        _client = new AvCodersTcpClient("127.0.0.1", port, "WorkerTestClient", CommandStringFormat.Ascii);

        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, 15);
            await TestNetwork.WaitUntilAsync(
                () => _client.ConnectionState == ConnectionState.Connected,
                15, "client never connected");

            _client.Disconnect();
            await TestNetwork.WaitUntilAsync(
                () => Workers(_client).All(w => !w.IsRunning),
                10, "not all workers stopped after Disconnect");

            _client.Connect();
            await TestNetwork.WaitUntilAsync(
                () => Workers(_client).All(w => w.IsRunning),
                10, "not all workers restarted after Connect");

            using var secondConnection = await TestNetwork.AcceptAsync(listener, 30);
            await TestNetwork.WaitUntilAsync(
                () => _client.ConnectionState == ConnectionState.Connected,
                15, "client never reconnected after Connect");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static List<ThreadWorker> Workers(AvCodersTcpClient client)
    {
        var workers = new List<ThreadWorker>();
        foreach (var name in new[] { "ReceiveThreadWorker", "ConnectionStateWorker", "SendQueueWorker" })
        {
            for (var type = client.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null)
                    continue;
                workers.Add((ThreadWorker)field.GetValue(client)!);
                break;
            }
        }
        Assert.Equal(3, workers.Count);
        return workers;
    }

    /// <summary>
    /// Reads until at least <paramref name="expectedAtLeast"/> bytes have arrived followed by
    /// five seconds of silence (long enough for a duplicate queue delivery to show up).
    /// </summary>
    private static async Task<int> ReadUntilQuietAsync(System.Net.Sockets.TcpClient serverSide, int expectedAtLeast)
    {
        var stream = serverSide.GetStream();
        var buffer = new byte[65536];
        var total = 0;
        var deadline = DateTime.UtcNow.AddSeconds(45);
        var lastData = DateTime.UtcNow;
        Task<int>? pendingRead = null;

        while (DateTime.UtcNow < deadline)
        {
            pendingRead ??= stream.ReadAsync(buffer, 0, buffer.Length);
            var completed = await Task.WhenAny(pendingRead, Task.Delay(250));
            if (completed == pendingRead)
            {
                var bytesRead = await pendingRead;
                pendingRead = null;
                if (bytesRead == 0)
                    break;
                total += bytesRead;
                lastData = DateTime.UtcNow;
            }
            else if (total >= expectedAtLeast && DateTime.UtcNow - lastData > TimeSpan.FromSeconds(5))
            {
                break;
            }
        }
        return total;
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
