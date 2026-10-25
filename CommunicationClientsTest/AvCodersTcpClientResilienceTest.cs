using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace AVCoders.CommunicationClients.Tests;

/// <summary>
/// Reconnection and send-queue behaviour. These tests exercise the connection-state
/// worker's real backoff/poll cycles, so their timeouts are generous by design.
/// </summary>
[Collection(LoggerFactoryCollection.Name)]
public class AvCodersTcpClientResilienceTest : IDisposable
{
    private const int QueueTimeoutSeconds = 10;
    private static readonly TimeSpan AgeMargin = TimeSpan.FromSeconds(2);
    private AvCodersTcpClient? _client;

    public void Dispose()
    {
        try { _client?.Disconnect(); } catch { /* test teardown */ }
        if (LogBase.LoggerFactory is ScopeCapturingLoggerFactory)
            LogBase.LoggerFactory = NullLoggerFactory.Instance;
    }

    private static string? TriggerIdOf(ScopeCapturingLoggerFactory logs, string instanceName, string messageStart) => logs.Logs
        .Where(log => Equals(log.Scope.GetValueOrDefault("InstanceName"), instanceName) && log.Message.StartsWith(messageStart))
        .Select(log => log.TriggerId)
        .FirstOrDefault();

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
    public async Task Send_QueuedWhileOffline_RaisesItsTxOnceAtWriteTimeWithTheSendersTriggerId()
    {
        var logs = new ScopeCapturingLoggerFactory();
        LogBase.LoggerFactory = logs;
        var port = TestNetwork.GetFreePort();
        _client = new AvCodersTcpClient("127.0.0.1", port, "QueuedTxTestClient", CommandStringFormat.Ascii);
        _client.SetQueueTimeout(60);
        var sent = new ConcurrentQueue<(string Message, string? TriggerId)>();
        var sentBytes = new ConcurrentQueue<string?>();
        _client.RequestHandlers += message => sent.Enqueue((message, Trigger.Id));
        _client.RequestByteHandlers += _ => sentBytes.Enqueue(Trigger.Id);

        var triggerId = Trigger.Start();
        _client.Send("queued message");
        Assert.Empty(sent);
        Assert.Empty(sentBytes);

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            using var serverSide = await TestNetwork.AcceptAsync(listener, 30);
            Assert.Equal("queued message", await TestNetwork.ReadStringAsync(serverSide, 30));
            await TestNetwork.WaitUntilAsync(() => !sent.IsEmpty, 10, "the queued send never raised its tx");
            await Task.Delay(TimeSpan.FromSeconds(3));

            Assert.Equal(("queued message", triggerId), Assert.Single(sent));
            Assert.Equal(triggerId, Assert.Single(sentBytes));
            Assert.Equal(triggerId, TriggerIdOf(logs, "QueuedTxTestClient", "Sending: queued message"));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Send_WhileConnected_RaisesItsTxWithTheCallersTriggerId()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        _client = new AvCodersTcpClient("127.0.0.1", port, "DirectTxTestClient", CommandStringFormat.Ascii);
        var sent = new ConcurrentQueue<(string Message, string? TriggerId)>();
        _client.RequestHandlers += message => sent.Enqueue((message, Trigger.Id));

        try
        {
            using var serverSide = await TestNetwork.AcceptAsync(listener, 15);
            await TestNetwork.WaitUntilAsync(
                () => _client.ConnectionState == ConnectionState.Connected,
                15, "client never connected");

            var triggerId = Trigger.Start();
            _client.Send("direct message");

            Assert.Equal(("direct message", triggerId), Assert.Single(sent));
            Assert.Equal("direct message", await TestNetwork.ReadStringAsync(serverSide, 10));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Send_DroppedFromTheQueue_RaisesNoTx()
    {
        var logs = new ScopeCapturingLoggerFactory();
        LogBase.LoggerFactory = logs;
        var port = TestNetwork.GetFreePort();
        _client = new AvCodersTcpClient("127.0.0.1", port, "DroppedTxTestClient", CommandStringFormat.Ascii);
        _client.SetQueueTimeout(0);
        var requests = 0;
        _client.RequestHandlers += _ => Interlocked.Increment(ref requests);
        _client.RequestByteHandlers += _ => Interlocked.Increment(ref requests);

        var triggerId = Trigger.Start();
        _client.Send("stale message");

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            using var serverSide = await TestNetwork.AcceptAsync(listener, 30);
            await TestNetwork.WaitUntilAsync(() => TestNetwork.SendQueueCount(_client) == 0, 10,
                "the stale message was never taken off the queue");
            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Equal(0, requests);
            Assert.Equal(0, serverSide.Available);
            Assert.Equal(triggerId, TriggerIdOf(logs, "DroppedTxTestClient", "Dropping queued message"));
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
    public async Task Client_ReconnectsWithinSeconds_WhenTheServerDropsAnEstablishedConnection()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        _client = new AvCodersTcpClient("127.0.0.1", port, "PromptReconnectTestClient", CommandStringFormat.Ascii);

        try
        {
            var firstConnection = await TestNetwork.AcceptAsync(listener, 15);
            await TestNetwork.WaitUntilAsync(
                () => _client.ConnectionState == ConnectionState.Connected,
                15, "client never connected the first time");
            await Task.Delay(TimeSpan.FromSeconds(3));

            firstConnection.Close();
            var sinceTheDrop = Stopwatch.StartNew();

            using var secondConnection = await TestNetwork.AcceptAsync(listener, 30);
            Assert.True(sinceTheDrop.Elapsed < TimeSpan.FromSeconds(5),
                $"the client took {sinceTheDrop.Elapsed} to reconnect after the drop");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Reconnect_AfterAGoodConnectionDrops_FirstRetryWaitsTheInitialBackoff()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        _client = new AvCodersTcpClient("127.0.0.1", port, "BackoffTestClient", CommandStringFormat.Ascii);
        var clock = Stopwatch.StartNew();
        var transitions = new ConcurrentQueue<(ConnectionState State, TimeSpan At)>();

        try
        {
            var firstConnection = await TestNetwork.AcceptAsync(listener, 15);
            await TestNetwork.WaitUntilAsync(
                () => _client.ConnectionState == ConnectionState.Connected,
                15, "client never connected");
            _client.ConnectionStateHandlers += state => transitions.Enqueue((state, clock.Elapsed));

            listener.Stop();
            firstConnection.Close();

            await TestNetwork.WaitUntilAsync(
                () => transitions.Count(t => t.State == ConnectionState.Connecting) >= 2,
                30, "the client never retried twice after the drop");
            var recorded = transitions.ToList();
            var firstAttempt = recorded.FindIndex(t => t.State == ConnectionState.Connecting);
            var firstFailure = recorded.FindIndex(firstAttempt, t => t.State == ConnectionState.Disconnected);
            var secondAttempt = recorded.FindIndex(firstFailure, t => t.State == ConnectionState.Connecting);
            var wait = recorded[secondAttempt].At - recorded[firstFailure].At;
            Assert.True(wait < TimeSpan.FromMilliseconds(1500),
                $"the first retry after the drop waited {wait}, not the initial 1 s backoff");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Send_WhenAQueuedWriteFails_RetriesItAheadOfTheMessagesQueuedAfterIt()
    {
        var port = TestNetwork.GetFreePort();
        _client = new AvCodersTcpClient("127.0.0.1", port, "RetryOrderTestClient", CommandStringFormat.Ascii);
        _client.SetQueueTimeout(60);
        var writeLock = GetWriteLock(_client);
        await writeLock.WaitAsync();
        _client.Send("first|");
        _client.Send("second|");
        _client.Send("third|");

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, 30);
            await TestNetwork.WaitUntilAsync(
                () => _client.Errors.Any(e => e.Message.StartsWith("Write timed out")),
                30, "the queued write never timed out");
            writeLock.Release();

            using var second = await TestNetwork.AcceptAsync(listener, 30);
            var received = await ReadUntilQuietAsync(second, "first|second|third|".Length);
            Assert.Equal("first|second|third|", System.Text.Encoding.ASCII.GetString(received));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Send_WhenAQueuedWriteFails_GetsTheWholeQueueTimeoutAfterTheReconnect()
    {
        var port = TestNetwork.GetFreePort();
        _client = new AvCodersTcpClient("127.0.0.1", port, "FreshTimestampTestClient", CommandStringFormat.Ascii);
        _client.SetQueueTimeout(QueueTimeoutSeconds);
        var sendQueueWorker = TestNetwork.SendQueueWorker(_client);
        await sendQueueWorker.Stop();
        var sent = new ConcurrentQueue<string>();
        _client.RequestHandlers += sent.Enqueue;
        _client.Send("requeued|");
        _client.Send("stale|");

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, 30);
            await TestNetwork.WaitUntilAsync(
                () => _client.ConnectionState == ConnectionState.Connected,
                15, "client never connected");
            var writeLock = GetWriteLock(_client);
            await writeLock.WaitAsync();
            TestNetwork.SetQueuedAge<(byte[], string?)>(_client, TimeSpan.FromSeconds(QueueTimeoutSeconds) - AgeMargin);

            await sendQueueWorker.Restart();
            await TestNetwork.WaitUntilAsync(
                () => _client.Errors.Any(e => e.Message.StartsWith("Write timed out")),
                30, "the queued write never timed out");
            writeLock.Release();

            using var second = await TestNetwork.AcceptAsync(listener, 30);
            Assert.Equal("requeued|", await TestNetwork.ReadStringAsync(second, 10));
            await TestNetwork.WaitUntilAsync(() => TestNetwork.SendQueueCount(_client) == 0, 10,
                "the message queued behind it was never taken off the queue");
            Assert.Equal(["requeued|"], sent);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Send_WhenTheWriteTimesOut_ReconnectsOnceForThatFailure()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        _client = new AvCodersTcpClient("127.0.0.1", port, "OneReconnectTestClient", CommandStringFormat.Ascii);
        var states = new ConcurrentQueue<ConnectionState>();

        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, 15);
            await TestNetwork.WaitUntilAsync(
                () => _client.ConnectionState == ConnectionState.Connected,
                15, "client never connected");
            _client.ConnectionStateHandlers += states.Enqueue;
            var writeLock = GetWriteLock(_client);
            await writeLock.WaitAsync();
            try
            {
                _client.Send("timeout-marker"u8.ToArray());
            }
            finally
            {
                writeLock.Release();
            }

            using var second = await TestNetwork.AcceptAsync(listener, 30);
            await TestNetwork.WaitUntilAsync(
                () => _client.ConnectionState == ConnectionState.Connected,
                15, "client never reconnected");
            Assert.Equal(1, states.Count(s => s == ConnectionState.Disconnecting));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Reconnect_CalledAgainForTheSameConnection_ClosesItOnce()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        _client = new AvCodersTcpClient("127.0.0.1", port, "ReconnectTwiceTestClient", CommandStringFormat.Ascii);
        var states = new ConcurrentQueue<ConnectionState>();

        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, 15);
            await TestNetwork.WaitUntilAsync(
                () => _client.ConnectionState == ConnectionState.Connected,
                15, "client never connected");
            _client.ConnectionStateHandlers += states.Enqueue;

            _client.Reconnect();
            _client.Reconnect();

            Assert.Equal(1, states.Count(s => s == ConnectionState.Disconnecting));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Reconnect_WhileAConnectionAttemptIsRunning_LeavesItRunning()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        _client = new AvCodersTcpClient("127.0.0.1", port, "ReconnectWhileConnectingTestClient", CommandStringFormat.Ascii);
        var backoffField = typeof(AvCodersTcpClient).GetField("_reconnectBackoffMs", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var states = new ConcurrentQueue<ConnectionState>();
        int? backoffAfterTheReconnect = null;

        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, 15);
            await TestNetwork.WaitUntilAsync(
                () => _client.ConnectionState == ConnectionState.Connected,
                15, "client never connected");
            _client.ConnectionStateHandlers += state =>
            {
                states.Enqueue(state);
                if (state != ConnectionState.Connecting || backoffAfterTheReconnect != null)
                    return;
                backoffField.SetValue(_client, 8000);
                _client.Reconnect();
                backoffAfterTheReconnect = (int)backoffField.GetValue(_client)!;
            };

            _client.Reconnect();

            using var secondConnection = await TestNetwork.AcceptAsync(listener, 15);
            await TestNetwork.WaitUntilAsync(
                () => _client.ConnectionState == ConnectionState.Connected,
                15, "client never reconnected");
            Assert.Equal(1000, backoffAfterTheReconnect);
            Assert.Equal(1, states.Count(s => s == ConnectionState.Connecting));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Reconnect_WhileNotConnected_StartsAConnectionAttemptAtOnce()
    {
        var port = TestNetwork.GetFreePort();
        _client = new AvCodersTcpClient("127.0.0.1", port, "ReconnectNowTestClient", CommandStringFormat.Ascii);
        var failures = 0;
        _client.ConnectionStateHandlers += state =>
        {
            if (state == ConnectionState.Disconnected)
                Interlocked.Increment(ref failures);
        };
        await TestNetwork.WaitUntilAsync(() => Volatile.Read(ref failures) >= 3, 60,
            "the client never failed three connection attempts");
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        try
        {
            var sinceTheReconnect = Stopwatch.StartNew();
            _client.Reconnect();

            using var connection = await TestNetwork.AcceptAsync(listener, 30);
            Assert.True(sinceTheReconnect.Elapsed < TimeSpan.FromMilliseconds(2500),
                $"the client took {sinceTheReconnect.Elapsed} to try again after Reconnect");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ConnectionWorkerStopped_WhileConnecting_LeavesTheClientDisconnected()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        _client = new AvCodersTcpClient("127.0.0.1", port, "StoppedWhileConnectingTestClient", CommandStringFormat.Ascii);
        var connectionWorker = TestNetwork.Workers(_client)[1];

        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, 15);
            await TestNetwork.WaitUntilAsync(
                () => _client.ConnectionState == ConnectionState.Connected,
                15, "client never connected");
            var stopRequested = false;
            _client.ConnectionStateHandlers += state =>
            {
                if (state != ConnectionState.Connecting || stopRequested)
                    return;
                stopRequested = true;
                _ = connectionWorker.Stop();
            };

            _client.Reconnect();

            await TestNetwork.WaitUntilAsync(() => stopRequested, 15, "the client never started a new attempt");
            await connectionWorker.Stop();
            Assert.Equal(ConnectionState.Disconnected, _client.ConnectionState);
            Assert.False(listener.Pending(), "the stopped attempt left a connection open");
        }
        finally
        {
            listener.Stop();
        }
    }

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
            var received = await ReadUntilQuietAsync(second, payload.Length);
            Assert.Equal(payload, received);
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

            var innerField = typeof(AvCodersTcpClient).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var inner = (System.Net.Sockets.TcpClient)innerField.GetValue(_client)!;
            var rejectingStream = new NetworkStream(inner.Client, ownsSocket: false);
            rejectingStream.Dispose();
            typeof(System.Net.Sockets.TcpClient).GetField("_dataStream", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(inner, rejectingStream);

            var payload = "reject-marker"u8.ToArray();
            _client.Send(payload);

            Assert.Contains(_client.Errors, error => error.Exception is ObjectDisposedException);
            using var second = await TestNetwork.AcceptAsync(listener, 30);
            var received = await ReadUntilQuietAsync(second, payload.Length);
            Assert.Equal(payload, received);
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
                () => TestNetwork.Workers(_client).All(w => !w.IsRunning),
                10, "not all workers stopped after Disconnect");

            _client.Connect();
            await TestNetwork.WaitUntilAsync(
                () => TestNetwork.Workers(_client).All(w => w.IsRunning),
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

    [Fact]
    public async Task Disconnect_ResolvesTheConnectionIssue()
    {
        _client = new AvCodersTcpClient("127.0.0.1", TestNetwork.GetFreePort(), "IssueTestClient",
            CommandStringFormat.Ascii);
        _client.ConnectionIssueThreshold = TimeSpan.Zero;
        await TestNetwork.WaitUntilAsync(
            () => _client.GetOngoingIssues().Any(i => i.Key == CommunicationClient.ConnectionIssueKey),
            30, "the refused connection never raised an ongoing issue");

        _client.Disconnect();

        Assert.Empty(_client.GetOngoingIssues());
        Assert.Equal(ConnectionState.Disconnected, _client.DesiredConnectionState);
    }

    [Fact]
    public async Task Disconnect_StaysDisconnected_ThroughReconnectAndSend()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        _client = new AvCodersTcpClient("127.0.0.1", port, "StayDownTestClient", CommandStringFormat.Ascii);

        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, 15);
            await TestNetwork.WaitUntilAsync(
                () => _client.ConnectionState == ConnectionState.Connected,
                15, "client never connected");
            var errorsBeforeDisconnect = _client.Errors.Count;

            _client.Disconnect();
            _client.Reconnect();
            _client.Send("dropped");
            await Task.Delay(TimeSpan.FromSeconds(3));

            Assert.False(listener.Pending(), "the client reconnected after Disconnect");
            Assert.Equal(ConnectionState.Disconnected, _client.ConnectionState);
            Assert.All(TestNetwork.Workers(_client), w => Assert.False(w.IsRunning));
            Assert.Equal(errorsBeforeDisconnect, _client.Errors.Count);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void Send_WhileDisconnectedOnRequest_IsDroppedNotQueued()
    {
        _client = new AvCodersTcpClient("127.0.0.1", TestNetwork.GetFreePort(), "DropTestClient",
            CommandStringFormat.Ascii);
        _client.Disconnect();
        var requests = 0;
        _client.RequestHandlers += _ => requests++;
        _client.RequestByteHandlers += _ => requests++;

        _client.Send("dropped");
        _client.Send("dropped"u8.ToArray());

        Assert.Equal(0, requests);
        Assert.Equal(0, TestNetwork.SendQueueCount(_client));
    }

    [Fact]
    public async Task Connect_AfterDisconnect_ResumesAutoReconnect()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        _client = new AvCodersTcpClient("127.0.0.1", port, "ResumeTestClient", CommandStringFormat.Ascii);

        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, 15);
            await TestNetwork.WaitUntilAsync(
                () => _client.ConnectionState == ConnectionState.Connected,
                15, "client never connected");
            _client.Disconnect();

            _client.Connect();
            var secondConnection = await TestNetwork.AcceptAsync(listener, 30);
            await TestNetwork.WaitUntilAsync(
                () => _client.ConnectionState == ConnectionState.Connected,
                15, "client never connected after Connect");
            secondConnection.Close();

            using var thirdConnection = await TestNetwork.AcceptAsync(listener, 30);
            await TestNetwork.WaitUntilAsync(
                () => _client.ConnectionState == ConnectionState.Connected,
                15, "client never reconnected after the server dropped it");
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Reads until at least <paramref name="expectedAtLeast"/> bytes have arrived followed by
    /// five seconds of silence (long enough for a duplicate queue delivery to show up).
    /// </summary>
    private static async Task<byte[]> ReadUntilQuietAsync(System.Net.Sockets.TcpClient serverSide, int expectedAtLeast)
    {
        var stream = serverSide.GetStream();
        var buffer = new byte[65536];
        var received = new List<byte>();
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
                received.AddRange(buffer.Take(bytesRead));
                lastData = DateTime.UtcNow;
            }
            else if (received.Count >= expectedAtLeast && DateTime.UtcNow - lastData > TimeSpan.FromSeconds(5))
            {
                break;
            }
        }
        return received.ToArray();
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
