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
    private static readonly TimeSpan MinimumBackoff = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReconnectLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EstablishedFor = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RefusalStep = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ConnectedCheckInterval = TimeSpan.FromSeconds(1);
    private const int AcceptWithinSeconds = 10;
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan DefaultQueueTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReconnectingSendQueueTimeout = TimeSpan.FromSeconds(15);
    private const int Races = 100;
    private static readonly Actor Panel = new(ActorKind.Panel, "192.0.2.40", "AvCodersTcpClientResilienceTest", "LECTERN-PANEL", "connection-2");
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

    private TimeSpan[] RetryWaits() => _time.ArmedTimers.Where(due => due >= MinimumBackoff && due < ConnectTimeout).ToArray();

    private async Task<TcpListener> ListenAndRetryAtOnce(ushort port)
    {
        var connectionWorker = TestNetwork.Workers(_client!)[1];
        await connectionWorker.Stop();
        var listener = TestNetwork.ListenOn(_time, port);
        await connectionWorker.Restart();
        return listener;
    }

    private async Task<System.Net.Sockets.TcpClient> AcceptWithoutTheClock(TcpListener listener)
    {
        var connection = await TestNetwork.AcceptAsync(listener, AcceptWithinSeconds);
        await Wait.UntilAsync(() => _client!.ConnectionState == ConnectionState.Connected, "the client never connected");
        return connection;
    }

    private async Task SendTheQueue(ThreadWorker sendQueueWorker)
    {
        await sendQueueWorker.Restart();
        await Wait.UntilAsync(() => TestNetwork.SendQueueCount(_client!) == 0, "the queued sends were never taken off the queue");
    }

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
            await Wait.UntilAsync(() => _time.ArmedTimers.Contains(ConnectedCheckInterval), "the client never armed its connection check");

            firstConnection.Close();
            var droppedAt = _time.GetUtcNow();

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
        var retries = 0;

        try
        {
            var firstConnection = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never connected");
            await TestNetwork.SendQueueWorker(_client!).Stop();
            _client!.ConnectionStateHandlers += state =>
            {
                if (state == ConnectionState.Connecting)
                    Interlocked.Increment(ref retries);
            };

            listener.Stop();
            _time.ConnectTimeouts = ConnectTimeouts.AtOnce;
            firstConnection.Close();
            await TestNetwork.UntilAsync(_time, () => Volatile.Read(ref retries) == 1, "the client never retried after the drop");
            await Wait.UntilAsync(() => RetryWaits().Length == 1, "the first retry never waited to retry");
            var wait = RetryWaits()[0];

            await Task.Run(() => _time.Advance(wait));
            await Wait.UntilAsync(() => Volatile.Read(ref retries) == 2, "the client never retried twice after the drop");
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

    [Fact]
    public void Disconnect_ByAPerson_TurnsReconnectOnSendOn()
    {
        CreateClientThatCannotConnect(out _, "PersonDisconnectTestClient");
        var raised = new ConcurrentQueue<bool>();
        _client!.ReconnectOnSendHandlers += raised.Enqueue;

        AsAPerson.Run(_client.Disconnect);

        Assert.True(_client.ReconnectOnSend);
        Assert.Equal(AsAPerson.Browser, _client.DisconnectedBy);
        Assert.Equal([true], raised);
    }

    [Fact]
    public async Task Disconnect_ByAPerson_RecordsWhoBeforeTheStateAndReasonHandlersRun()
    {
        CreateClientThatCannotConnect(out _, "DisconnectOrderTestClient");
        await UntilTheFirstAttemptFailed();
        var seen = new ConcurrentQueue<(Actor? DisconnectedBy, bool ReconnectOnSend)>();
        _client!.ConnectionStateHandlers += _ => seen.Enqueue((_client.DisconnectedBy, _client.ReconnectOnSend));
        _client.ConnectionReasonHandlers += _ => seen.Enqueue((_client.DisconnectedBy, _client.ReconnectOnSend));

        AsAPerson.Run(_client.Disconnect);

        Assert.NotEmpty(seen);
        Assert.All(seen, handled => Assert.Equal((AsAPerson.Browser, true), handled));
    }

    [Fact]
    public void Disconnect_WithNoPerson_LeavesReconnectOnSendOff()
    {
        CreateClientThatCannotConnect(out _, "ProgramDisconnectTestClient");

        _client!.Disconnect();

        Assert.False(_client.ReconnectOnSend);
        Assert.Null(_client.DisconnectedBy);
    }

    [Fact]
    public void Disconnect_ByTheProgramAfterAPerson_KeepsWhoDisconnectedItAndReconnectOnSend()
    {
        CreateClientThatCannotConnect(out _, "ProgramAfterPersonTestClient");
        AsAPerson.Run(_client!.Disconnect);

        _client.Disconnect();

        Assert.Equal(AsAPerson.Browser, _client.DisconnectedBy);
        Assert.True(_client.ReconnectOnSend);
    }

    [Fact]
    public void Disconnect_ByASecondPerson_KeepsTheFirst()
    {
        CreateClientThatCannotConnect(out _, "SecondPersonTestClient");
        AsAPerson.Run(_client!.Disconnect);

        AsAPerson.Run(Panel, _client.Disconnect);

        Assert.Equal(AsAPerson.Browser, _client.DisconnectedBy);
        Assert.True(_client.ReconnectOnSend);
    }

    [Fact]
    public void Disconnect_ByAPersonAfterStayDisconnected_KeepsReconnectOnSendOff()
    {
        CreateClientThatCannotConnect(out _, "StayDisconnectedThenPersonTestClient");
        AsAPerson.Run(_client!.Disconnect);
        _client.SetReconnectOnSend(false);

        AsAPerson.Run(Panel, _client.Disconnect);

        Assert.Equal(AsAPerson.Browser, _client.DisconnectedBy);
        Assert.False(_client.ReconnectOnSend);
    }

    [Fact]
    public void Disconnect_ByAPersonAfterAConnect_TurnsReconnectOnSendOnAgain()
    {
        CreateClientThatCannotConnect(out _, "SecondDisconnectTestClient");
        AsAPerson.Run(_client!.Disconnect);
        _client.SetReconnectOnSend(false);
        _client.Connect();

        AsAPerson.Run(Panel, _client.Disconnect);

        Assert.True(_client.ReconnectOnSend);
        Assert.Equal(Panel, _client.DisconnectedBy);
    }

    [Fact]
    public void Connect_AfterStayDisconnected_RaisesTheChangeOfWhoDisconnectedIt()
    {
        CreateClientThatCannotConnect(out _, "WhoChangedTestClient");
        AsAPerson.Run(_client!.Disconnect);
        _client.SetReconnectOnSend(false);
        var raised = new ConcurrentQueue<(bool ReconnectOnSend, Actor? DisconnectedBy)>();
        _client.ReconnectOnSendHandlers += reconnectOnSend => raised.Enqueue((reconnectOnSend, _client.DisconnectedBy));

        _client.Connect();

        Assert.Equal([(false, null)], raised);
    }

    [Fact]
    public async Task Send_ByAPersonAfterAPersonDisconnected_ReconnectsAndDeliversIt()
    {
        var listener = StartListener(out var port);
        CreateClient(port, "ReconnectOnSendTestClient");
        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never connected");
            AsAPerson.Run(_client!.Disconnect);

            AsAPerson.Run(() => _client.Send("power on"));

            using var secondConnection = await TestNetwork.AcceptAsync(listener, _time);
            Assert.Equal("power on", await TestNetwork.ReadStringAsync(secondConnection, _time));
            Assert.Equal(ConnectionState.Connected, _client.DesiredConnectionState);
            Assert.False(_client.ReconnectOnSend);
            Assert.Null(_client.DisconnectedBy);
            Assert.False(_client.ReconnectingForSend);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Send_FromAPollAfterAPersonDisconnected_StaysDisconnected()
    {
        var listener = StartListener(out var port);
        CreateClient(port, "PollWhileDisconnectedTestClient");
        var polled = false;
        var poll = new ThreadWorker(_ =>
        {
            _client!.Send("poll");
            polled = true;
            return Task.CompletedTask;
        }, PollInterval);
        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never connected");
            AsAPerson.Run(_client!.Disconnect);

            await AsAPerson.RunAsync(poll.Restart);
            await Wait.UntilAsync(() => polled, "the poll never ran");
            await TestNetwork.Advance(_time, EstablishedFor);

            Assert.False(listener.Pending(), "a poll reconnected the client");
            Assert.Equal(ConnectionState.Disconnected, _client.DesiredConnectionState);
            Assert.Equal(0, TestNetwork.SendQueueCount(_client));
            Assert.True(_client.ReconnectOnSend);
        }
        finally
        {
            await poll.Stop();
            listener.Stop();
        }
    }

    [Fact]
    public async Task Send_ByAPersonWithReconnectOnSendOff_StaysDisconnected()
    {
        var listener = StartListener(out var port);
        CreateClient(port, "StayDisconnectedTestClient");
        try
        {
            using var firstConnection = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("client never connected");
            AsAPerson.Run(_client!.Disconnect);
            _client.SetReconnectOnSend(false);

            AsAPerson.Run(() => _client.Send("power on"));
            await TestNetwork.Advance(_time, EstablishedFor);

            Assert.False(listener.Pending(), "the client reconnected with reconnect on send off");
            Assert.Equal(ConnectionState.Disconnected, _client.DesiredConnectionState);
            Assert.Equal(0, TestNetwork.SendQueueCount(_client));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Disconnect_RacingAPersonsSend_LeavesTheWorkersMatchingTheDesiredState()
    {
        CreateClientThatCannotConnect(out _, "DisconnectRaceTestClient");
        await UntilTheFirstAttemptFailed();

        for (var race = 0; race < Races; race++)
        {
            _client!.Connect();
            AsAPerson.Run(_client.Disconnect);
            using var start = new Barrier(2);

            await Task.WhenAll(
                Task.Run(() =>
                {
                    start.SignalAndWait();
                    AsAPerson.Run(() => _client.Send("power on"));
                }),
                Task.Run(() =>
                {
                    start.SignalAndWait();
                    _client.Disconnect();
                }));

            var connected = _client.DesiredConnectionState == ConnectionState.Connected;
            await Wait.UntilAsync(() => TestNetwork.Workers(_client).All(worker => worker.IsRunning == connected),
                $"race {race}: the workers don't match the desired state {_client.DesiredConnectionState}");
        }
    }

    [Fact]
    public async Task Connect_RacingAPersonsSend_KeepsTheSend()
    {
        CreateClientThatCannotConnect(out _, "ConnectRaceTestClient");
        await UntilTheFirstAttemptFailed();

        for (var race = 0; race < Races; race++)
        {
            _client!.Connect();
            AsAPerson.Run(_client.Disconnect);
            using var start = new Barrier(2);

            await Task.WhenAll(
                Task.Run(() =>
                {
                    start.SignalAndWait();
                    AsAPerson.Run(() => _client.Send("power on"));
                }),
                Task.Run(() =>
                {
                    start.SignalAndWait();
                    _client.Connect();
                }));

            Assert.Equal(1, TestNetwork.SendQueueCount(_client));
        }
    }

    [Fact]
    public async Task Connect_AfterAPersonsSendStartedTheReconnect_DeliversTheSend()
    {
        CreateClientThatCannotConnect(out var port, "ConnectAfterSendTestClient");
        await UntilTheFirstAttemptFailed();
        AsAPerson.Run(_client!.Disconnect);
        AsAPerson.Run(() => _client.Send("power on|"));

        _client.Connect();

        var sendQueueWorker = TestNetwork.SendQueueWorker(_client);
        await sendQueueWorker.Stop();
        var listener = await ListenAndRetryAtOnce(port);
        try
        {
            using var connection = await AcceptWithoutTheClock(listener);
            _client.Send(Sentinel);
            await SendTheQueue(sendQueueWorker);
            Assert.Equal("power on|" + Sentinel, await TestNetwork.ReadUntilAsync(connection, _time, Sentinel));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Send_ByTwoPeopleAtOnceAfterAPersonDisconnected_ReconnectsOnceAndDeliversBoth()
    {
        CreateClientThatCannotConnect(out var port, "TwoSendersTestClient");
        await UntilTheFirstAttemptFailed();
        AsAPerson.Run(_client!.Disconnect);
        _client.ClearEvents();
        using var start = new Barrier(2);

        await Task.WhenAll(
            Task.Run(() =>
            {
                start.SignalAndWait();
                AsAPerson.Run(() => _client.Send("power on|"));
            }),
            Task.Run(() =>
            {
                start.SignalAndWait();
                AsAPerson.Run(Panel, () => _client.Send("input 2|"));
            }));

        Assert.Single(_client.Events, e => e.Info == "Connect requested");
        var listener = TestNetwork.ListenOn(_time, port);
        try
        {
            using var connection = await TestNetwork.AcceptAsync(listener, _time);
            await UntilConnected("the client never connected");
            _client.Send(Sentinel);
            var received = await TestNetwork.ReadUntilAsync(connection, _time, Sentinel);
            Assert.Contains(received, new[] { "power on|input 2|" + Sentinel, "input 2|power on|" + Sentinel });
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task<string> ReceivedAfterWaiting(TimeSpan wait, Action queue)
    {
        await UntilTheFirstAttemptFailed();
        var sendQueueWorker = TestNetwork.SendQueueWorker(_client!);
        queue();
        await sendQueueWorker.Stop();
        var queuedAt = _time.GetUtcNow();

        var listener = await ListenAndRetryAtOnce(_client!.Port);
        try
        {
            using var connection = await AcceptWithoutTheClock(listener);
            await TestNetwork.Advance(_time, queuedAt + wait - _time.GetUtcNow());
            _client.Send(Sentinel);
            await SendTheQueue(sendQueueWorker);
            return await TestNetwork.ReadUntilAsync(connection, _time, Sentinel);
        }
        finally
        {
            listener.Stop();
        }
    }

    private void APressOfTwoCommandsAndAPollDuringTheReconnect()
    {
        AsAPerson.Run(() =>
        {
            _client!.Disconnect();
            _client.Send("power on|");
            _client.Send("input 2|");
        });
        _client!.Send("poll|");
    }

    [Fact]
    public async Task Send_ByAPersonDuringTheReconnect_WaitsUpToFifteenSeconds_WhileAPollKeepsTheQueueTimeout()
    {
        CreateClientThatCannotConnect(out _, "ReconnectingSendWaitTestClient");

        var received = await ReceivedAfterWaiting(ReconnectingSendQueueTimeout - AgeMargin, APressOfTwoCommandsAndAPollDuringTheReconnect);

        Assert.Equal("power on|input 2|" + Sentinel, received);
    }

    [Fact]
    public async Task Send_ByAPersonDuringTheReconnect_IsDroppedAfterFifteenSeconds()
    {
        CreateClientThatCannotConnect(out _, "ReconnectingSendDroppedTestClient");

        var received = await ReceivedAfterWaiting(ReconnectingSendQueueTimeout + AgeMargin, APressOfTwoCommandsAndAPollDuringTheReconnect);

        Assert.Equal(Sentinel, received);
    }

    [Fact]
    public async Task Send_ByAPersonDuringTheReconnect_KeepsALongerQueueTimeout()
    {
        CreateClientThatCannotConnect(out _, "ReconnectingSendLongerTimeoutTestClient");
        _client!.SetQueueTimeout(60);

        var received = await ReceivedAfterWaiting(ReconnectingSendQueueTimeout + AgeMargin, APressOfTwoCommandsAndAPollDuringTheReconnect);

        Assert.Equal("power on|input 2|poll|" + Sentinel, received);
    }

    [Fact]
    public async Task Send_ByAPersonWhileOfflineWithNoReconnectForSend_KeepsTheQueueTimeout()
    {
        CreateClientThatCannotConnect(out _, "OfflinePersonSendTestClient");

        var received = await ReceivedAfterWaiting(DefaultQueueTimeout + AgeMargin, () => AsAPerson.Run(() => _client!.Send("power on|")));

        Assert.Equal(Sentinel, received);
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
