using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AVCoders.CommunicationClients.Tests;

public class AvCodersTcpClientResilienceTest : IDisposable
{
    private const int QueueTimeoutSeconds = 10;
    private const string Sentinel = "end|";
    private static readonly TimeSpan AgeMargin = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InitialBackoffLimit = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ReconnectLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EstablishedFor = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RefusalStep = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ConnectedCheckInterval = TimeSpan.FromSeconds(1);
    private const int AcceptWithinSeconds = 10;
    private readonly ConnectTimeoutClock _time = new();
    private AvCodersTcpClient? _client;

    public void Dispose()
    {
        try { _client?.Disconnect(); } catch { }
    }

    private AvCodersTcpClient CreateClient(ushort port, string name) =>
        _client = new AvCodersTcpClient("127.0.0.1", port, name, CommandStringFormat.Ascii, _time);

    private AvCodersTcpClient CreateClientThatCannotConnect(out ushort port, string name)
    {
        port = TestNetwork.GetFreePort();
        _time.ConnectTimeouts = ConnectTimeouts.AtOnce;
        return CreateClient(port, name);
    }

    private static TcpListener StartListener(out ushort port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private Task UntilConnected(string failureMessage) =>
        TestNetwork.UntilAsync(_time, () => _client!.ConnectionState == ConnectionState.Connected, failureMessage);

    private Task UntilTheFirstAttemptFailed() =>
        Wait.UntilAsync(() => _client!.ConnectionState == ConnectionState.Disconnected, "the first connection attempt never failed");

    private static SemaphoreSlim GetWriteLock(AvCodersTcpClient client)
    {
        var field = typeof(AvCodersTcpClient).GetField("_writeLock", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (SemaphoreSlim)field.GetValue(client)!;
    }

    [Fact]
    public async Task Send_WhileDisconnected_QueuesAndDeliversAfterConnect()
    {
        CreateClientThatCannotConnect(out var port, "QueueTestClient");
        _client!.SetQueueTimeout(60);

        _client.Send("queued message");
        await UntilTheFirstAttemptFailed();

        var listener = TestNetwork.ListenOn(_time, port);
        try
        {
            using var serverSide = await TestNetwork.AcceptAsync(listener, _time);
            Assert.Equal("queued message", await TestNetwork.ReadStringAsync(serverSide, _time));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Send_WhileConnected_RaisesItsTxWithTheCallersTriggerId()
    {
        var listener = StartListener(out var port);
        CreateClient(port, "DirectTxTestClient");
        var sent = new ConcurrentQueue<(string Message, string? TriggerId)>();
        _client!.RequestHandlers += message => sent.Enqueue((message, Trigger.Id));

        try
        {
            using var serverSide = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never connected");

            var triggerId = Trigger.Start();
            _client.Send("direct message");

            Assert.Equal(("direct message", triggerId), Assert.Single(sent));
            Assert.Equal("direct message", await TestNetwork.ReadStringAsync(serverSide, _time));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Client_Reconnects_AfterServerDropsTheConnection()
    {
        var listener = StartListener(out var port);
        CreateClient(port, "ReconnectTestClient");

        try
        {
            var firstConnection = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never connected the first time");

            firstConnection.Close();

            using var secondConnection = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never reported Connected after reconnecting");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Client_ReconnectsWithinSeconds_WhenTheServerDropsAnEstablishedConnection()
    {
        var listener = StartListener(out var port);
        CreateClient(port, "PromptReconnectTestClient");

        try
        {
            var firstConnection = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never connected the first time");
            await TestNetwork.Advance(_time, EstablishedFor);

            firstConnection.Close();
            var droppedAt = _time.GetUtcNow();
            await Wait.UntilAsync(() => _client!.ConnectionState != ConnectionState.Connected, "the client never noticed the drop");

            await Task.Run(() => _time.Advance(ReconnectLimit - TestNetwork.ClockStep));
            using var secondConnection = await TestNetwork.AcceptAsync(listener, AcceptWithinSeconds);
            var reconnectedAfter = _time.GetUtcNow() - droppedAt;
            Assert.True(reconnectedAfter < ReconnectLimit, $"the client took {reconnectedAfter} to reconnect after the drop");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Reconnect_AfterAGoodConnectionDrops_FirstRetryWaitsTheInitialBackoff()
    {
        var listener = StartListener(out var port);
        CreateClient(port, "BackoffTestClient");
        var transitions = new ConcurrentQueue<(ConnectionState State, DateTimeOffset At)>();

        try
        {
            var firstConnection = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never connected");
            _client!.ConnectionStateHandlers += state => transitions.Enqueue((state, _time.GetUtcNow()));

            listener.Stop();
            _time.ConnectTimeouts = ConnectTimeouts.AtOnce;
            firstConnection.Close();
            await Wait.UntilAsync(() => _client.ConnectionState != ConnectionState.Connected, "the client never noticed the drop");

            await Task.Run(() => _time.Advance(ConnectedCheckInterval));
            await Wait.UntilAsync(() => transitions.Count(t => t.State == ConnectionState.Connecting) == 1
                                        && transitions.Last().State == ConnectionState.Disconnected,
                "the client never retried after the drop");
            await Task.Run(() => _time.Advance(InitialBackoffLimit - TestNetwork.ClockStep));
            await Wait.UntilAsync(() => transitions.Count(t => t.State == ConnectionState.Connecting) >= 2,
                "the client never retried twice after the drop");
            var recorded = transitions.ToList();
            var firstAttempt = recorded.FindIndex(t => t.State == ConnectionState.Connecting);
            var firstFailure = recorded.FindIndex(firstAttempt, t => t.State == ConnectionState.Disconnected);
            var secondAttempt = recorded.FindIndex(firstFailure, t => t.State == ConnectionState.Connecting);
            var wait = recorded[secondAttempt].At - recorded[firstFailure].At;
            Assert.True(wait < InitialBackoffLimit, $"the first retry after the drop waited {wait}, not the initial 1 s backoff");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Send_WhenAQueuedWriteFails_RetriesItAheadOfTheMessagesQueuedAfterIt()
    {
        CreateClientThatCannotConnect(out var port, "RetryOrderTestClient");
        _client!.SetQueueTimeout(60);
        var writeLock = GetWriteLock(_client);
        await writeLock.WaitAsync();
        _client.Send("first|");
        _client.Send("second|");
        _client.Send("third|");
        await UntilTheFirstAttemptFailed();

        var listener = TestNetwork.ListenOn(_time, port);
        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, _time);
            await TestNetwork.UntilAsync(_time,
                () => _client.Errors.Any(e => e.Message.StartsWith("Write timed out")),
                "the queued write never timed out");
            writeLock.Release();

            using var second = await TestNetwork.AcceptAsync(listener, _time);
            var queued = await TestNetwork.ReadUntilAsync(second, _time, "third|");
            _client.Send(Sentinel);
            Assert.Equal("first|second|third|" + Sentinel, queued + await TestNetwork.ReadUntilAsync(second, _time, Sentinel));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Send_WhenAQueuedWriteFails_GetsTheWholeQueueTimeoutAfterTheReconnect()
    {
        CreateClientThatCannotConnect(out var port, "FreshTimestampTestClient");
        _client!.SetQueueTimeout(QueueTimeoutSeconds);
        var sendQueueWorker = TestNetwork.SendQueueWorker(_client);
        await sendQueueWorker.Stop();
        var sent = new ConcurrentQueue<string>();
        _client.RequestHandlers += sent.Enqueue;
        await UntilTheFirstAttemptFailed();
        var queuedAt = _time.GetUtcNow();
        _client.Send("requeued|");
        _client.Send("stale|");

        var listener = TestNetwork.ListenOn(_time, port);
        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never connected");
            var writeLock = GetWriteLock(_client);
            await writeLock.WaitAsync();
            await TestNetwork.Advance(_time, queuedAt + TimeSpan.FromSeconds(QueueTimeoutSeconds) - AgeMargin - _time.GetUtcNow());

            await sendQueueWorker.Restart();
            await TestNetwork.UntilAsync(_time,
                () => _client.Errors.Any(e => e.Message.StartsWith("Write timed out")),
                "the queued write never timed out");
            writeLock.Release();

            using var second = await TestNetwork.AcceptAsync(listener, _time);
            Assert.Equal("requeued|", await TestNetwork.ReadStringAsync(second, _time));
            await TestNetwork.UntilAsync(_time, () => TestNetwork.SendQueueCount(_client) == 0,
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
        var listener = StartListener(out var port);
        CreateClient(port, "OneReconnectTestClient");
        var states = new ConcurrentQueue<ConnectionState>();

        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never connected");
            _client!.ConnectionStateHandlers += states.Enqueue;
            var writeLock = GetWriteLock(_client);
            await writeLock.WaitAsync();
            try
            {
                var send = Task.Run(() => _client.Send("timeout-marker"u8.ToArray()));
                await TestNetwork.UntilAsync(_time, () => send.IsCompleted, "Send never gave up on the write");
            }
            finally
            {
                writeLock.Release();
            }

            using var second = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never reconnected");
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
        var listener = StartListener(out var port);
        CreateClient(port, "ReconnectTwiceTestClient");
        var states = new ConcurrentQueue<ConnectionState>();

        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never connected");
            _client!.ConnectionStateHandlers += states.Enqueue;

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
        var listener = StartListener(out var port);
        CreateClient(port, "ReconnectWhileConnectingTestClient");
        var backoffField = typeof(AvCodersTcpClient).GetField("_reconnectBackoffMs", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var states = new ConcurrentQueue<ConnectionState>();
        int? backoffAfterTheReconnect = null;

        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never connected");
            _client!.ConnectionStateHandlers += state =>
            {
                states.Enqueue(state);
                if (state != ConnectionState.Connecting || backoffAfterTheReconnect != null)
                    return;
                backoffField.SetValue(_client, 8000);
                _client.Reconnect();
                backoffAfterTheReconnect = (int)backoffField.GetValue(_client)!;
            };

            _client.Reconnect();

            using var secondConnection = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never reconnected");
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
        CreateClientThatCannotConnect(out var port, "ReconnectNowTestClient");
        var failures = 0;
        _client!.ConnectionStateHandlers += state =>
        {
            if (state == ConnectionState.Disconnected)
                Interlocked.Increment(ref failures);
        };
        await Wait.UntilAsync(_time, RefusalStep, () => Volatile.Read(ref failures) >= 3,
            "the client never failed three connection attempts");
        var listener = TestNetwork.ListenOn(_time, port);

        try
        {
            _client.Reconnect();

            using var connection = await TestNetwork.AcceptAsync(listener, 15);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ConnectionWorkerStopped_WhileConnecting_LeavesTheClientDisconnected()
    {
        var listener = StartListener(out var port);
        CreateClient(port, "StoppedWhileConnectingTestClient");
        var connectionWorker = TestNetwork.Workers(_client!)[1];

        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never connected");
            var stopRequested = false;
            _client!.ConnectionStateHandlers += state =>
            {
                if (state != ConnectionState.Connecting || stopRequested)
                    return;
                stopRequested = true;
                _ = connectionWorker.Stop();
            };

            _client.Reconnect();

            await Wait.UntilAsync(() => stopRequested, "the client never started a new attempt");
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
        var listener = StartListener(out var port);
        CreateClient(port, "TimeoutTestClient");
        _client!.SetQueueTimeout(60);

        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never connected");

            var writeLock = GetWriteLock(_client);
            await writeLock.WaitAsync();
            var sendStarted = _time.GetUtcNow();
            try
            {
                var send = Task.Run(() => _client.Send("timeout-marker|"u8.ToArray()));
                await TestNetwork.UntilAsync(_time, () => send.IsCompleted, "Send blocked the caller instead of timing out");
            }
            finally
            {
                writeLock.Release();
            }
            var blockedFor = _time.GetUtcNow() - sendStarted;
            Assert.True(blockedFor >= WriteTimeout, $"Send returned after {blockedFor} without waiting for the write timeout");

            firstConnection.Close();

            using var second = await TestNetwork.AcceptAsync(listener, _time);
            var requeued = await TestNetwork.ReadUntilAsync(second, _time, "timeout-marker|");
            _client.Send(Sentinel);
            Assert.Equal("timeout-marker|" + Sentinel, requeued + await TestNetwork.ReadUntilAsync(second, _time, Sentinel));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Send_WhenTheSocketRejectsTheWrite_DeliversThePayloadOnceAfterReconnect()
    {
        var listener = StartListener(out var port);
        CreateClient(port, "RejectTestClient");
        _client!.SetQueueTimeout(60);

        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never connected");

            var innerField = typeof(AvCodersTcpClient).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var inner = (System.Net.Sockets.TcpClient)innerField.GetValue(_client)!;
            var rejectingStream = new NetworkStream(inner.Client, ownsSocket: false);
            rejectingStream.Dispose();
            typeof(System.Net.Sockets.TcpClient).GetField("_dataStream", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(inner, rejectingStream);

            _client.Send("reject-marker|"u8.ToArray());

            Assert.Contains(_client.Errors, error => error.Exception is ObjectDisposedException);
            using var second = await TestNetwork.AcceptAsync(listener, _time);
            var requeued = await TestNetwork.ReadUntilAsync(second, _time, "reject-marker|");
            _client.Send(Sentinel);
            Assert.Equal("reject-marker|" + Sentinel, requeued + await TestNetwork.ReadUntilAsync(second, _time, Sentinel));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Disconnect_StopsAllWorkers_AndConnectRestartsThem()
    {
        var listener = StartListener(out var port);
        CreateClient(port, "WorkerTestClient");

        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never connected");

            _client!.Disconnect();
            await Wait.UntilAsync(() => TestNetwork.Workers(_client).All(w => !w.IsRunning),
                "not all workers stopped after Disconnect");

            _client.Connect();
            await Wait.UntilAsync(() => TestNetwork.Workers(_client).All(w => w.IsRunning),
                "not all workers restarted after Connect");

            using var secondConnection = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never reconnected after Connect");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Disconnect_ResolvesTheConnectionIssue()
    {
        CreateClientThatCannotConnect(out _, "IssueTestClient");
        _client!.ConnectionIssueThreshold = TimeSpan.Zero;
        await Wait.UntilAsync(
            () => _client.GetOngoingIssues().Any(i => i.Key == CommunicationClient.ConnectionIssueKey),
            "the failed connection never raised an ongoing issue");

        _client.Disconnect();

        Assert.Empty(_client.GetOngoingIssues());
        Assert.Equal(ConnectionState.Disconnected, _client.DesiredConnectionState);
    }

    [Fact]
    public async Task Disconnect_StaysDisconnected_ThroughReconnectAndSend()
    {
        var listener = StartListener(out var port);
        CreateClient(port, "StayDownTestClient");

        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never connected");
            var errorsBeforeDisconnect = _client!.Errors.Count;

            _client.Disconnect();
            _client.Reconnect();
            _client.Send("dropped");
            await TestNetwork.Advance(_time, EstablishedFor);

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
        CreateClientThatCannotConnect(out _, "DropTestClient");
        _client!.Disconnect();
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
        var listener = StartListener(out var port);
        CreateClient(port, "ResumeTestClient");

        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never connected");
            _client!.Disconnect();

            _client.Connect();
            var secondConnection = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never connected after Connect");
            secondConnection.Close();

            using var thirdConnection = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never reconnected after the server dropped it");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task FailedConnection_ReportsDisconnected_NotError()
    {
        CreateClientThatCannotConnect(out _, "FailedTestClient");

        await Wait.UntilAsync(() => _client!.ConnectionState == ConnectionState.Disconnected,
            "client never reported Disconnected after a failed connection");
    }

    [Fact]
    public void Constructor_SetsTheDesiredConnectionStateToConnected()
    {
        CreateClientThatCannotConnect(out _, "DesiredStateTestClient");

        Assert.Equal(ConnectionState.Connected, _client!.DesiredConnectionState);
    }

    [Fact]
    public async Task AFailedAttempt_SetsTheReason_UntilTheClientConnects()
    {
        CreateClientThatCannotConnect(out var port, "ReasonTestClient");
        await Wait.UntilAsync(() => _client!.ConnectionReason != null, "the failed attempt never set a reason");
        var reason = _client!.ConnectionReason!;
        var listener = TestNetwork.ListenOn(_time, port);

        try
        {
            using var connection = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never connected");

            Assert.Equal(ConnectionReasonCategory.TimedOut, reason.Category);
            Assert.Equal($"The connection attempt to 127.0.0.1:{port} timed out", reason.Description);
            Assert.Contains("CanceledException", reason.RawException);
            await Wait.UntilAsync(() => _client.ConnectionReason == null, "connecting never cleared the reason");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Disconnect_AfterAFailedAttempt_IsRequested()
    {
        CreateClientThatCannotConnect(out _, "RequestedReasonTestClient");
        await UntilTheFirstAttemptFailed();

        _client!.Disconnect();

        Assert.Equal(ConnectionReasonCategory.Requested, _client.ConnectionReason!.Category);
        Assert.Equal("Disconnected on request", _client.ConnectionReason.Description);
    }
}

[Collection(LoggerFactoryCollection.Name)]
public class AvCodersTcpClientLogTest : IDisposable
{
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(4);
    private readonly ConnectTimeoutClock _time = new() { ConnectTimeouts = ConnectTimeouts.AtOnce };
    private readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly AvCodersTcpClient _client;
    private readonly ushort _port = TestNetwork.GetFreePort();

    public AvCodersTcpClientLogTest()
    {
        LogBase.LoggerFactory = _logs;
        _client = new AvCodersTcpClient("127.0.0.1", _port, "LogTestClient", CommandStringFormat.Ascii, _time);
    }

    public void Dispose()
    {
        try { _client.Disconnect(); } catch { }
        LogBase.LoggerFactory = NullLoggerFactory.Instance;
    }

    private Task UntilTheFirstAttemptFailed() =>
        Wait.UntilAsync(() => _client.ConnectionState == ConnectionState.Disconnected, "the first connection attempt never failed");

    private string? TriggerIdOf(string messageStart) => _logs.Logs
        .Where(log => Equals(log.Scope.GetValueOrDefault("InstanceName"), _client.Name) && log.Message.StartsWith(messageStart))
        .Select(log => log.TriggerId)
        .FirstOrDefault();

    [Fact]
    public async Task Send_QueuedWhileOffline_RaisesItsTxOnceAtWriteTimeWithTheSendersTriggerId()
    {
        _client.SetQueueTimeout(60);
        var sent = new ConcurrentQueue<(string Message, string? TriggerId)>();
        var sentBytes = new ConcurrentQueue<string?>();
        _client.RequestHandlers += message => sent.Enqueue((message, Trigger.Id));
        _client.RequestByteHandlers += _ => sentBytes.Enqueue(Trigger.Id);

        var triggerId = Trigger.Start();
        _client.Send("queued message");
        Assert.Empty(sent);
        Assert.Empty(sentBytes);
        await UntilTheFirstAttemptFailed();

        var listener = TestNetwork.ListenOn(_time, _port);
        try
        {
            using var serverSide = await TestNetwork.AcceptAsync(listener, _time);
            Assert.Equal("queued message", await TestNetwork.ReadStringAsync(serverSide, _time));
            await TestNetwork.UntilAsync(_time, () => !sent.IsEmpty, "the queued send never raised its tx");
            await TestNetwork.Advance(_time, DuplicateWindow);

            Assert.Equal(("queued message", triggerId), Assert.Single(sent));
            Assert.Equal(triggerId, Assert.Single(sentBytes));
            Assert.Equal(triggerId, TriggerIdOf("Sending: queued message"));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Send_DroppedFromTheQueue_RaisesNoTx()
    {
        _client.SetQueueTimeout(0);
        var requests = 0;
        _client.RequestHandlers += _ => Interlocked.Increment(ref requests);
        _client.RequestByteHandlers += _ => Interlocked.Increment(ref requests);

        var triggerId = Trigger.Start();
        _client.Send("stale message");
        await UntilTheFirstAttemptFailed();

        var listener = TestNetwork.ListenOn(_time, _port);
        try
        {
            using var serverSide = await TestNetwork.AcceptAsync(listener, _time);
            await TestNetwork.UntilAsync(_time, () => TriggerIdOf("Dropping queued message") != null,
                "the stale message was never dropped from the queue");

            Assert.Equal(0, requests);
            Assert.Equal(0, serverSide.Available);
            Assert.Equal(triggerId, TriggerIdOf("Dropping queued message"));
        }
        finally
        {
            listener.Stop();
        }
    }
}

[Collection(LoggerFactoryCollection.Name)]
public class AvCodersTcpClientConnectFailureLogTest : IDisposable
{
    private readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly ushort _port = TestNetwork.GetFreePort();
    private AvCodersTcpClient? _client;

    public AvCodersTcpClientConnectFailureLogTest()
    {
        LogBase.LoggerFactory = _logs;
    }

    public void Dispose()
    {
        try { _client?.Disconnect(); } catch { }
        LogBase.LoggerFactory = NullLoggerFactory.Instance;
    }

    private AvCodersTcpClient CreateClient(string name, TimeProvider time) =>
        _client = new AvCodersTcpClient("127.0.0.1", _port, name, CommandStringFormat.Ascii, time);

    private Task UntilTheConnectionIssueIsLogged() =>
        Wait.UntilAsync(() => _client!.GetIssues().Any(issue => issue.Key == CommunicationClient.ConnectionIssueKey)
                              && ClientLogs().Any(log => log.Level == LogLevel.Warning),
            "the failed attempt never raised and logged the connection issue");

    private List<CapturedLog> ClientLogs() => _logs.Logs
        .Where(log => Equals(log.Scope.GetValueOrDefault("InstanceName"), _client!.Name))
        .ToList();

    [Fact]
    public async Task ConnectTimeout_LogsTheExceptionAtDebug_AndStillWarnsAndRaisesTheIssue()
    {
        CreateClient("TimeoutLogTestClient", new ConnectTimeoutClock { ConnectTimeouts = ConnectTimeouts.AtOnce });

        await UntilTheConnectionIssueIsLogged();

        var error = Assert.Single(_client!.Errors);
        Assert.IsAssignableFrom<OperationCanceledException>(error.Exception);
        var logs = ClientLogs();
        Assert.DoesNotContain(logs, log => log.Level == LogLevel.Error);
        Assert.Contains(logs, log => log.Level == LogLevel.Debug && log.Message == error.Message);
        Assert.Contains(logs, log => log.Level == LogLevel.Warning && log.Message == $"The connection attempt to 127.0.0.1:{_port} timed out");
    }

    [Fact]
    public async Task ConnectTimeout_IsTimedOut()
    {
        CreateClient("TimeoutReasonTestClient", new ConnectTimeoutClock { ConnectTimeouts = ConnectTimeouts.AtOnce });

        await UntilTheConnectionIssueIsLogged();

        Assert.Equal(ConnectionReasonCategory.TimedOut, _client!.ConnectionReason!.Category);
        Assert.Equal($"The connection attempt to 127.0.0.1:{_port} timed out", _client.ConnectionReason.Description);
    }

    [Fact]
    public async Task RefusedConnection_StillLogsTheExceptionAsAnError()
    {
        CreateClient("RefusedLogTestClient", new FakeTimeProvider());

        await UntilTheConnectionIssueIsLogged();

        var error = Assert.Single(_client!.Errors);
        Assert.Equal(SocketError.ConnectionRefused, Assert.IsType<SocketException>(error.Exception).SocketErrorCode);
        var logs = ClientLogs();
        Assert.Contains(logs, log => log.Level == LogLevel.Error && log.Message == error.Message);
        Assert.Contains(logs, log => log.Level == LogLevel.Warning && log.Message == $"127.0.0.1:{_port} refused the connection");
        Assert.Equal(ConnectionReasonCategory.Refused, _client.ConnectionReason!.Category);
        Assert.Equal($"127.0.0.1:{_port} refused the connection", _client.ConnectionReason.Description);
        Assert.StartsWith("System.Net.Sockets.SocketException: ", _client.ConnectionReason.RawException);
        await Wait.UntilAsync(() => _client.ConnectionState == ConnectionState.Disconnected,
            "client never reported Disconnected after a refused connection");
    }
}
