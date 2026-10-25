using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using TcpClient = System.Net.Sockets.TcpClient;

namespace AVCoders.CommunicationClients.Tests;

public class AvCodersTcpServerTest : IDisposable
{
    private readonly AvCodersTcpServer _server;
    private readonly ushort _port;
    private readonly List<TcpClient> _testClients = new();

    public AvCodersTcpServerTest()
    {
        _port = TestNetwork.GetFreePort();
        _server = new AvCodersTcpServer(_port, "TestTcpServer", CommandStringFormat.Ascii);
    }

    private Task<TcpClient> ConnectClientAsync() => ConnectClientAsync(_port);

    private static bool Listening(AvCodersTcpServer server) =>
        (bool)typeof(AvCodersTcpServer)
            .GetField("_listening", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;

    private async Task<TcpClient> ConnectClientAsync(ushort port)
    {
        // The server binds its listener on the ConnectionStateWorker shortly after
        // construction, so retry until the port accepts connections.
        var client = new TcpClient();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            try
            {
                await client.ConnectAsync("127.0.0.1", port);
                break;
            }
            catch (SocketException)
            {
                if (DateTime.UtcNow > deadline)
                    throw;
                await Task.Delay(250);
            }
        }
        _testClients.Add(client);
        return client;
    }

    public void Dispose()
    {
        foreach (var client in _testClients)
        {
            try { client.Dispose(); } catch { /* test teardown */ }
        }
        try { _server.Disconnect(); } catch { /* test teardown */ }
    }

    [Fact]
    public async Task Server_ReportsConnected_WhenAClientConnects()
    {
        await ConnectClientAsync();
        await TestNetwork.WaitUntilAsync(
            () => _server.ConnectionState == ConnectionState.Connected,
            15, "server never reported Connected after a client connected");
    }

    [Fact]
    public async Task ClientData_InvokesResponseHandlers()
    {
        string? response = null;
        _server.ResponseHandlers += message => response = message;

        var client = await ConnectClientAsync();
        await TestNetwork.WaitUntilAsync(
            () => _server.ConnectionState == ConnectionState.Connected,
            15, "server never accepted the client");

        await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes("hello server"));

        await TestNetwork.WaitUntilAsync(() => response != null, 15, "response handler never invoked");
        Assert.Equal("hello server", response);
    }

    [Fact]
    public async Task EachRead_FromOneClient_GetsItsOwnTriggerId()
    {
        var triggerIds = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        _server.ResponseHandlers += _ => triggerIds.Enqueue(Trigger.Id);

        var client = await ConnectClientAsync();
        await TestNetwork.WaitUntilAsync(
            () => _server.ConnectionState == ConnectionState.Connected,
            15, "server never accepted the client");

        await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes("first"));
        await TestNetwork.WaitUntilAsync(() => triggerIds.Count == 1, 15, "the first read never reached the handlers");
        await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes("second"));
        await TestNetwork.WaitUntilAsync(() => triggerIds.Count == 2, 15, "the second read never reached the handlers");

        Assert.All(triggerIds, Assert.NotNull);
        Assert.Equal(2, triggerIds.Distinct().Count());
    }

    [Fact]
    public async Task SingleNullProbeByte_IsIgnored()
    {
        var responses = new List<string>();
        _server.ResponseHandlers += message => responses.Add(message);

        var client = await ConnectClientAsync();
        await TestNetwork.WaitUntilAsync(
            () => _server.ConnectionState == ConnectionState.Connected,
            15, "server never accepted the client");

        // A lone 0x00 is the keep-alive probe and must not reach handlers.
        // The pause keeps the two writes from coalescing into one TCP read -
        // the server's pending ReadAsync consumes the probe byte immediately.
        await client.GetStream().WriteAsync(new byte[] { 0x00 });
        await Task.Delay(250);
        // Real data afterwards must arrive, which also proves the pipeline was alive.
        await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes("real data"));

        await TestNetwork.WaitUntilAsync(() => responses.Count > 0, 15, "response handler never invoked");
        Assert.Single(responses);
        Assert.Equal("real data", responses[0]);
    }

    [Fact]
    public void Send_WithNoClients_RaisesNoTx()
    {
        var tx = new List<string>();
        _server.RequestHandlers += message => tx.Add(message);
        _server.RequestByteHandlers += bytes => tx.Add(BitConverter.ToString(bytes));

        _server.Send("nobody is listening");
        _server.Send([0x01, 0x02]);

        Assert.Empty(tx);
    }

    [Fact]
    public async Task Send_BroadcastsToAllConnectedClients()
    {
        var client1 = await ConnectClientAsync();
        var client2 = await ConnectClientAsync();
        await TestNetwork.WaitUntilAsync(
            () => _server.ConnectionState == ConnectionState.Connected,
            15, "server never accepted the clients");

        var read1 = TestNetwork.ReadStringAsync(client1, 30);
        var read2 = TestNetwork.ReadStringAsync(client2, 30);

        // The accept loop admits one client per second, so resend until both
        // clients are registered and have received the broadcast.
        for (var i = 0; i < 30 && !(read1.IsCompleted && read2.IsCompleted); i++)
        {
            _server.Send("broadcast");
            await Task.Delay(250);
        }

        Assert.Contains("broadcast", await read1);
        Assert.Contains("broadcast", await read2);
    }

    [Fact]
    public async Task ClientData_PassesBytesAbove0x7FThroughAsTheSameCharacters()
    {
        var responses = new ConcurrentQueue<string>();
        var reads = new ConcurrentQueue<byte[]>();
        _server.ResponseHandlers += message => responses.Enqueue(message);
        _server.ResponseByteHandlers += bytes => reads.Enqueue(bytes);
        var payload = Enumerable.Range(0x80, 0x80).Select(value => (byte)value).ToArray();

        var client = await ConnectClientAsync();
        await TestNetwork.WaitUntilAsync(
            () => _server.ConnectionState == ConnectionState.Connected,
            15, "server never accepted the client");

        await client.GetStream().WriteAsync(payload);

        await TestNetwork.WaitUntilAsync(() => reads.Sum(read => read.Length) == payload.Length, 15, "the bytes never all arrived");
        Assert.Equal(new string(payload.Select(value => (char)value).ToArray()), string.Concat(responses));
    }

    [Fact]
    public async Task ClientData_DecodesEachReadOnItsOwn()
    {
        var responses = new ConcurrentQueue<string>();
        var reads = new ConcurrentQueue<byte[]>();
        _server.ResponseHandlers += message => responses.Enqueue(message);
        _server.ResponseByteHandlers += bytes => reads.Enqueue(bytes);

        var client = await ConnectClientAsync();
        await TestNetwork.WaitUntilAsync(
            () => _server.ConnectionState == ConnectionState.Connected,
            15, "server never accepted the client");

        await client.GetStream().WriteAsync(new byte[] { 0xE2 });
        await TestNetwork.WaitUntilAsync(() => reads.Count == 1, 15, "the first byte never arrived");
        await client.GetStream().WriteAsync(new byte[] { 0x82, 0xAC });

        await TestNetwork.WaitUntilAsync(() => reads.Count == 2, 15, "the second read never arrived");
        Assert.Equal(new[] { "\u00e2", "\u0082\u00ac" }, responses);
    }

    [Fact]
    public async Task Text_RoundTripsThroughOneEncoding()
    {
        string? response = null;
        _server.ResponseHandlers += message => response = message;

        var client = await ConnectClientAsync();
        await TestNetwork.WaitUntilAsync(
            () => _server.ConnectionState == ConnectionState.Connected,
            15, "server never accepted the client");

        _server.Send("\u0080caf\u00e9 \u00ff");
        var buffer = new byte[64];
        var read = client.GetStream().ReadAsync(buffer, 0, buffer.Length);
        Assert.True(await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(15))) == read, "the client never received the text");
        var sent = buffer.Take(await read).ToArray();
        await client.GetStream().WriteAsync(sent);

        await TestNetwork.WaitUntilAsync(() => response != null, 15, "the echoed text never arrived");
        Assert.Equal(new byte[] { 0x80, 0x63, 0x61, 0x66, 0xE9, 0x20, 0xFF }, sent);
        Assert.Equal("\u0080caf\u00e9 \u00ff", response);
    }

    [Fact]
    public async Task Send_ReplacesCharactersAbove0xFFWithAQuestionMark()
    {
        var tx = new ConcurrentQueue<string>();
        _server.RequestHandlers += message => tx.Enqueue(message);

        var client = await ConnectClientAsync();
        await TestNetwork.WaitUntilAsync(
            () => _server.ConnectionState == ConnectionState.Connected,
            15, "server never accepted the client");

        _server.Send("A\u20acB");

        var buffer = new byte[16];
        var read = client.GetStream().ReadAsync(buffer, 0, buffer.Length);
        Assert.True(await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(15))) == read, "the client never received the text");
        Assert.Equal(new byte[] { 0x41, 0x3F, 0x42 }, buffer[..await read]);
        Assert.Equal(new[] { "A\u20acB" }, tx);
    }

    [Fact]
    public async Task ClientData_ForAHexServer_IsDumpedAsHex()
    {
        var port = TestNetwork.GetFreePort();
        var server = new AvCodersTcpServer(port, "HexServer", CommandStringFormat.Hex);
        string? response = null;
        server.ResponseHandlers += message => response = message;
        try
        {
            var client = await ConnectClientAsync(port);
            await TestNetwork.WaitUntilAsync(
                () => server.ConnectionState == ConnectionState.Connected,
                15, "server never accepted the client");

            await client.GetStream().WriteAsync(new byte[] { 0x01, 0xAB });

            await TestNetwork.WaitUntilAsync(() => response != null, 15, "response handler never invoked");
            Assert.Equal("\\x01 \\xAB", response);
        }
        finally
        {
            server.Disconnect();
        }
    }

    [Fact]
    public async Task Reconnect_KeepsTheClientsAndKeepsAccepting()
    {
        var responses = new ConcurrentQueue<string>();
        _server.ResponseHandlers += message => responses.Enqueue(message);
        var kept = await ConnectClientAsync();
        await TestNetwork.WaitUntilAsync(
            () => _server.ConnectionState == ConnectionState.Connected,
            15, "server never accepted the client");

        _server.Reconnect();

        await kept.GetStream().WriteAsync(Encoding.ASCII.GetBytes("still connected"));
        await TestNetwork.WaitUntilAsync(() => responses.Contains("still connected"), 15, "the kept client's data never arrived");
        var added = await ConnectClientAsync();
        await added.GetStream().WriteAsync(Encoding.ASCII.GetBytes("accepted after reconnect"));
        await TestNetwork.WaitUntilAsync(() => responses.Contains("accepted after reconnect"), 15, "the server stopped accepting after reconnect");
        Assert.Empty(_server.Errors);
    }

    [Fact]
    public async Task Disconnect_StopsListeningAndDropsTheClients_AndConnectListensAgain()
    {
        var responses = new ConcurrentQueue<string>();
        _server.ResponseHandlers += message => responses.Enqueue(message);
        var dropped = await ConnectClientAsync();
        await TestNetwork.WaitUntilAsync(
            () => _server.ConnectionState == ConnectionState.Connected,
            15, "server never accepted the client");

        _server.Disconnect();

        Assert.Equal(ConnectionState.Disconnected, _server.DesiredConnectionState);
        Assert.Equal(ConnectionState.Disconnected, _server.ConnectionState);
        Assert.Equal("", await TestNetwork.ReadStringAsync(dropped, 15));
        using (var refused = new TcpClient())
            await Assert.ThrowsAnyAsync<SocketException>(() => refused.ConnectAsync("127.0.0.1", _port));

        _server.Connect();

        Assert.Equal(ConnectionState.Connected, _server.DesiredConnectionState);
        var accepted = await ConnectClientAsync();
        await accepted.GetStream().WriteAsync(Encoding.ASCII.GetBytes("after connect"));
        await TestNetwork.WaitUntilAsync(() => responses.Contains("after connect"), 15, "the accepted client's data never arrived");
    }

    [Fact]
    public async Task TryStartListener_AfterADisconnect_DiscardsTheListener()
    {
        await TestNetwork.WaitUntilAsync(() => Listening(_server), 15, "the server never listened");
        _server.Disconnect();

        var started = (bool)typeof(AvCodersTcpServer)
            .GetMethod("TryStartListener", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_server, null)!;

        Assert.False(started);
        Assert.False(Listening(_server));
        using var refused = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => refused.ConnectAsync("127.0.0.1", _port));
    }

    [Fact]
    public async Task ConnectionStateSubscriber_CanWaitForADisconnectOnAnotherThread()
    {
        var disconnectFinished = new TaskCompletionSource<bool>();
        _server.ConnectionStateHandlers += state =>
        {
            if (state != ConnectionState.Connected || disconnectFinished.Task.IsCompleted)
                return;
            var disconnect = new Thread(_server.Disconnect);
            disconnect.Start();
            disconnectFinished.TrySetResult(disconnect.Join(TimeSpan.FromSeconds(2)));
        };

        await ConnectClientAsync();

        Assert.True(await disconnectFinished.Task.WaitAsync(TimeSpan.FromSeconds(15)),
            "The subscriber was still holding a lock the disconnect needed");
        Assert.Equal(ConnectionState.Disconnected, _server.ConnectionState);
    }

    [Fact]
    public async Task DeadListener_AfterADisconnect_IsIgnored()
    {
        await TestNetwork.WaitUntilAsync(() => Listening(_server), 15, "the server never listened");
        _server.Disconnect();

        typeof(AvCodersTcpServer)
            .GetMethod("HandleDeadListener", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_server, [new ObjectDisposedException("listener"), CancellationToken.None]);

        Assert.DoesNotContain(_server.Errors, error => error.Message == "Listener failed, it will be rebound");
        Assert.False(TestNetwork.Workers(_server)[1].IsRunning, "the connection worker was restarted after the disconnect");
        Assert.Equal(ConnectionState.Disconnected, _server.ConnectionState);
    }

    [Fact]
    public async Task LastClientLeaving_ReportsDisconnected()
    {
        var client = await ConnectClientAsync();
        await TestNetwork.WaitUntilAsync(
            () => _server.ConnectionState == ConnectionState.Connected,
            15, "server never accepted the client");

        client.Dispose();

        await TestNetwork.WaitUntilAsync(
            () => _server.ConnectionState == ConnectionState.Disconnected,
            10, "the server still reported Connected after its last client left");
    }

    [Fact]
    public async Task DeadListener_RaisesTheConnectionIssueAndRebindsAtOnce()
    {
        var states = new ConcurrentQueue<ConnectionState>();
        _server.ConnectionStateHandlers += state => states.Enqueue(state);
        await ConnectClientAsync();
        await TestNetwork.WaitUntilAsync(
            () => _server.ConnectionState == ConnectionState.Connected,
            15, "server never accepted the client");
        var listener = (TcpListener)typeof(AvCodersTcpServer)
            .GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_server)!;

        listener.Stop();

        await TestNetwork.WaitUntilAsync(() => states.Contains(ConnectionState.Degraded), 10, "the dead listener never reported Degraded");
        Assert.Contains(_server.GetIssues(), issue => issue.Key == CommunicationClient.ConnectionIssueKey);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        using var rebound = new TcpClient();
        while (true)
        {
            try
            {
                await rebound.ConnectAsync("127.0.0.1", _port);
                break;
            }
            catch (SocketException)
            {
                Assert.True(DateTime.UtcNow < deadline, "the listener was not rebound at once");
                await Task.Delay(250);
            }
        }
    }

    [Fact]
    public async Task Send_ToAClientThatStopsReading_TimesOutAndDropsIt()
    {
        await ConnectClientAsync();
        await TestNetwork.WaitUntilAsync(
            () => _server.ConnectionState == ConnectionState.Connected,
            15, "server never accepted the client");
        var chunk = new byte[1024 * 1024];
        bool SendFailed() => _server.Errors.Any(error => error.Message.StartsWith("Sending to client"));

        var sends = Task.Run(() =>
        {
            for (var i = 0; i < 256 && !SendFailed(); i++)
                _server.Send(chunk);
        });

        Assert.True(await Task.WhenAny(sends, Task.Delay(TimeSpan.FromSeconds(30))) == sends, "Send never returned");
        Assert.True(SendFailed(), "no send to the client that stopped reading failed");
        await TestNetwork.WaitUntilAsync(
            () => _server.ConnectionState == ConnectionState.Disconnected,
            10, "the client that stopped reading was never dropped");
    }
}

public class AvCodersTcpServerResilienceTest
{
    private static TcpListener OccupyPort(out ushort port)
    {
        TcpListener blocker = new TcpListener(IPAddress.Any, 0);
        // Exclusive use so the server's ReuseAddress bind genuinely fails while we hold the port
        blocker.ExclusiveAddressUse = true;
        blocker.Start();
        port = (ushort)((IPEndPoint)blocker.LocalEndpoint).Port;
        return blocker;
    }

    private static async Task ConnectWithRetry(TcpClient client, ushort port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                if (DateTime.UtcNow > deadline)
                    throw;
                await Task.Delay(250);
            }
        }
    }

    [Fact]
    public async Task Constructor_DoesNotThrow_WhenPortIsInUse()
    {
        var blocker = OccupyPort(out var port);
        try
        {
            var server = new AvCodersTcpServer(port, "test", CommandStringFormat.Ascii);
            await Task.Delay(500); // Let the worker attempt (and fail) the bind
            Assert.NotEqual(ConnectionState.Connected, server.ConnectionState);
            server.Send("x"); // Must not throw while unbound
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public async Task Server_SelfHeals_AfterPortIsReleased()
    {
        var blocker = OccupyPort(out var port);
        var server = new AvCodersTcpServer(port, "test", CommandStringFormat.Ascii);
        var connected = new ManualResetEventSlim();
        server.ConnectionStateHandlers += state =>
        {
            if (state == ConnectionState.Connected)
                connected.Set();
        };

        await Task.Delay(1500); // Let at least one bind attempt fail
        Assert.NotEqual(ConnectionState.Connected, server.ConnectionState);
        blocker.Stop();

        using var client = new TcpClient();
        await ConnectWithRetry(client, port, TimeSpan.FromSeconds(30));
        Assert.True(connected.Wait(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Server_ReceivesData_OnFreePort()
    {
        var blocker = OccupyPort(out var port);
        blocker.Stop();

        var server = new AvCodersTcpServer(port, "test", CommandStringFormat.Ascii);
        var received = new ManualResetEventSlim();
        string? payload = null;
        server.ResponseHandlers += value =>
        {
            payload = value;
            received.Set();
        };

        // The bind happens on the worker shortly after construction, so poll the connect
        using var client = new TcpClient();
        await ConnectWithRetry(client, port, TimeSpan.FromSeconds(10));
        await client.GetStream().WriteAsync("hello"u8.ToArray());

        Assert.True(received.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal("hello", payload);
    }

    [Fact]
    public async Task BindFailure_RaisesTheConnectionIssue()
    {
        var blocker = OccupyPort(out var port);
        var server = new AvCodersTcpServer(port, "test", CommandStringFormat.Ascii);
        try
        {
            await TestNetwork.WaitUntilAsync(
                () => server.GetIssues().Any(issue => issue.Key == CommunicationClient.ConnectionIssueKey),
                10, "the failed bind raised no connection issue");
            var issue = server.GetIssues().Single(issue => issue.Key == CommunicationClient.ConnectionIssueKey);
            Assert.StartsWith($"The connection to Any:{port} failed", issue.Message);
        }
        finally
        {
            server.Disconnect();
            blocker.Stop();
        }
    }
}
