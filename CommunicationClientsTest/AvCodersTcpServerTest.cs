using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using TcpClient = System.Net.Sockets.TcpClient;

namespace AVCoders.CommunicationClients.Tests;

public class AvCodersTcpServerTest : IDisposable
{
    private const int MaxChunks = 256;
    private const int ChunkSize = 1024 * 1024;
    private const int ServerWriteTimeoutMs = 5000;
    private const int ShortWriteTimeoutMs = 100;
    private static readonly TimeSpan SendsGiveUpWithin = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectRetryWait = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan ConnectDeadline = TimeSpan.FromSeconds(15);
    private static readonly FieldInfo ClientsField = typeof(AvCodersTcpServer).GetField("_clients", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private readonly FakeTimeProvider _time = new();
    private readonly AvCodersTcpServer _server;
    private readonly ushort _port;
    private readonly List<TcpClient> _testClients = new();

    public AvCodersTcpServerTest()
    {
        _port = TestNetwork.GetFreePort();
        _server = new AvCodersTcpServer(_port, "TestTcpServer", CommandStringFormat.Ascii, _time);
    }

    private Task<TcpClient> ConnectClientAsync() => ConnectClientAsync(_port);

    private async Task<TcpClient> ConnectClientAsync(ushort port)
    {
        var deadline = DateTime.UtcNow + ConnectDeadline;
        while (true)
        {
            var client = TestNetwork.RefusedAtOnce();
            try
            {
                await client.ConnectAsync("127.0.0.1", port);
                _testClients.Add(client);
                return client;
            }
            catch (SocketException)
            {
                client.Dispose();
                if (DateTime.UtcNow > deadline)
                    throw;
                await Task.Delay(ConnectRetryWait);
            }
        }
    }

    private TcpClient AcceptedClient() =>
        Assert.Single(((ConcurrentDictionary<Guid, TcpClient>)ClientsField.GetValue(_server)!).Values);

    private Task UntilAsync(Func<bool> condition, string failureMessage) =>
        TestNetwork.UntilAsync(_time, condition, failureMessage);

    private Task UntilConnected() =>
        UntilAsync(() => _server.ConnectionState == ConnectionState.Connected, "server never accepted the client");

    public void Dispose()
    {
        foreach (var client in _testClients)
        {
            try { client.Dispose(); } catch { }
        }
        try { _server.Disconnect(); } catch { }
    }

    [Fact]
    public async Task Server_ReportsConnected_WhenAClientConnects()
    {
        await ConnectClientAsync();

        await UntilConnected();
    }

    [Fact]
    public async Task ClientData_InvokesResponseHandlers()
    {
        string? response = null;
        _server.ResponseHandlers += message => response = message;
        var client = await ConnectClientAsync();
        await UntilConnected();

        await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes("hello server"));

        await Wait.UntilAsync(() => response != null, "response handler never invoked");
        Assert.Equal("hello server", response);
    }

    [Fact]
    public async Task EachRead_FromOneClient_GetsItsOwnTriggerId()
    {
        var triggerIds = new ConcurrentQueue<string?>();
        _server.ResponseHandlers += _ => triggerIds.Enqueue(Trigger.Id);
        var client = await ConnectClientAsync();
        await UntilConnected();

        await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes("first"));
        await Wait.UntilAsync(() => triggerIds.Count == 1, "the first read never reached the handlers");
        await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes("second"));
        await Wait.UntilAsync(() => triggerIds.Count == 2, "the second read never reached the handlers");

        Assert.All(triggerIds, Assert.NotNull);
        Assert.Equal(2, triggerIds.Distinct().Count());
    }

    [Fact]
    public async Task SingleNullProbeByte_IsIgnored()
    {
        var responses = new ConcurrentQueue<string>();
        _server.ResponseHandlers += message => responses.Enqueue(message);
        var client = await ConnectClientAsync();
        await UntilConnected();

        var accepted = AcceptedClient();
        await client.GetStream().WriteAsync(new byte[] { 0x00 });
        await Wait.UntilAsync(() => accepted.Available == 0, "the server never read the probe byte");
        await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes("real data"));

        await Wait.UntilAsync(() => !responses.IsEmpty, "response handler never invoked");
        Assert.Equal("real data", Assert.Single(responses));
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
        await UntilConnected();

        var read1 = TestNetwork.ReadStringAsync(client1, 30);
        var read2 = TestNetwork.ReadStringAsync(client2, 30);

        await UntilAsync(() =>
        {
            _server.Send("broadcast");
            return read1.IsCompleted && read2.IsCompleted;
        }, "the broadcast never reached both clients");

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
        await UntilConnected();

        await client.GetStream().WriteAsync(payload);

        await Wait.UntilAsync(() => reads.Sum(read => read.Length) == payload.Length, "the bytes never all arrived");
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
        await UntilConnected();

        await client.GetStream().WriteAsync(new byte[] { 0xE2 });
        await Wait.UntilAsync(() => reads.Count == 1, "the first byte never arrived");
        await client.GetStream().WriteAsync(new byte[] { 0x82, 0xAC });

        await Wait.UntilAsync(() => reads.Count == 2, "the second read never arrived");
        Assert.Equal(new[] { "â", "\u0082¬" }, responses);
    }

    [Fact]
    public async Task Text_RoundTripsThroughOneEncoding()
    {
        string? response = null;
        _server.ResponseHandlers += message => response = message;
        var client = await ConnectClientAsync();
        await UntilConnected();

        _server.Send("\u0080café ÿ");
        var buffer = new byte[64];
        var read = client.GetStream().ReadAsync(buffer, 0, buffer.Length);
        Assert.True(await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(15))) == read, "the client never received the text");
        var sent = buffer.Take(await read).ToArray();
        await client.GetStream().WriteAsync(sent);

        await Wait.UntilAsync(() => response != null, "the echoed text never arrived");
        Assert.Equal(new byte[] { 0x80, 0x63, 0x61, 0x66, 0xE9, 0x20, 0xFF }, sent);
        Assert.Equal("\u0080café ÿ", response);
    }

    [Fact]
    public async Task ClientData_ForAHexServer_IsDumpedAsHex()
    {
        var port = TestNetwork.GetFreePort();
        var server = new AvCodersTcpServer(port, "HexServer", CommandStringFormat.Hex, _time);
        string? response = null;
        server.ResponseHandlers += message => response = message;
        try
        {
            var client = await ConnectClientAsync(port);
            await UntilAsync(() => server.ConnectionState == ConnectionState.Connected, "server never accepted the client");

            await client.GetStream().WriteAsync(new byte[] { 0x01, 0xAB });

            await Wait.UntilAsync(() => response != null, "response handler never invoked");
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
        await UntilConnected();

        _server.Reconnect();

        await kept.GetStream().WriteAsync(Encoding.ASCII.GetBytes("still connected"));
        await Wait.UntilAsync(() => responses.Contains("still connected"), "the kept client's data never arrived");
        var added = await ConnectClientAsync();
        await added.GetStream().WriteAsync(Encoding.ASCII.GetBytes("accepted after reconnect"));
        await UntilAsync(() => responses.Contains("accepted after reconnect"), "the server stopped accepting after reconnect");
        Assert.Empty(_server.Errors);
    }

    [Fact]
    public async Task Disconnect_StopsListeningAndDropsTheClients_AndConnectListensAgain()
    {
        var responses = new ConcurrentQueue<string>();
        _server.ResponseHandlers += message => responses.Enqueue(message);
        var dropped = await ConnectClientAsync();
        await UntilConnected();

        _server.Disconnect();

        Assert.Equal(ConnectionState.Disconnected, _server.DesiredConnectionState);
        Assert.Equal(ConnectionState.Disconnected, _server.ConnectionState);
        Assert.Equal("", await TestNetwork.ReadStringAsync(dropped, 15));
        using (var refused = TestNetwork.RefusedAtOnce())
            await Assert.ThrowsAnyAsync<SocketException>(() => refused.ConnectAsync("127.0.0.1", _port));

        _server.Connect();

        Assert.Equal(ConnectionState.Connected, _server.DesiredConnectionState);
        var accepted = await ConnectClientAsync();
        await accepted.GetStream().WriteAsync(Encoding.ASCII.GetBytes("after connect"));
        await UntilAsync(() => responses.Contains("after connect"), "the accepted client's data never arrived");
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

        await UntilAsync(() => disconnectFinished.Task.IsCompleted, "the server never accepted the client");
        Assert.True(await disconnectFinished.Task, "The subscriber was still holding a lock the disconnect needed");
        Assert.Equal(ConnectionState.Disconnected, _server.ConnectionState);
    }

    [Fact]
    public async Task LastClientLeaving_ReportsDisconnected()
    {
        var client = await ConnectClientAsync();
        await UntilConnected();

        client.Dispose();

        await Wait.UntilAsync(() => _server.ConnectionState == ConnectionState.Disconnected,
            "the server still reported Connected after its last client left");
    }

    [Fact]
    public async Task DeadListener_RaisesTheConnectionIssueAndRebindsAtOnce()
    {
        var states = new ConcurrentQueue<ConnectionState>();
        _server.ConnectionStateHandlers += state => states.Enqueue(state);
        await ConnectClientAsync();
        await UntilConnected();
        var listener = (TcpListener)typeof(AvCodersTcpServer)
            .GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_server)!;

        listener.Stop();

        await UntilAsync(() => states.Contains(ConnectionState.Degraded), "the dead listener never reported Degraded");
        Assert.Contains(_server.GetIssues(), issue => issue.Key == CommunicationClient.ConnectionIssueKey);
        await ConnectClientAsync();
    }

    [Fact]
    public async Task Send_ToAClientThatStopsReading_TimesOutAndDropsIt()
    {
        await ConnectClientAsync();
        await UntilConnected();
        var accepted = AcceptedClient();
        Assert.Equal(ServerWriteTimeoutMs, accepted.SendTimeout);
        accepted.SendTimeout = ShortWriteTimeoutMs;
        var chunk = new byte[ChunkSize];
        bool SendFailed() => _server.Errors.Any(error => error.Message.StartsWith("Sending to client"));

        var sends = Task.Run(() =>
        {
            for (var i = 0; i < MaxChunks && !SendFailed(); i++)
                _server.Send(chunk);
        });

        await sends.WaitAsync(SendsGiveUpWithin);
        var failure = Assert.Single(_server.Errors, error => error.Message.StartsWith("Sending to client"));
        var timedOut = Assert.IsType<SocketException>(Assert.IsType<IOException>(failure.Exception).InnerException);
        Assert.Equal(SocketError.TimedOut, timedOut.SocketErrorCode);
        await Wait.UntilAsync(() => _server.ConnectionState == ConnectionState.Disconnected,
            "the client that stopped reading was never dropped");
    }
}

public class AvCodersTcpServerResilienceTest
{
    private readonly FakeTimeProvider _time = new();

    private static TcpListener OccupyPort(out ushort port)
    {
        TcpListener blocker = new TcpListener(IPAddress.Any, 0);
        blocker.ExclusiveAddressUse = true;
        blocker.Start();
        port = (ushort)((IPEndPoint)blocker.LocalEndpoint).Port;
        return blocker;
    }

    private static bool HasTheConnectionIssue(AvCodersTcpServer server) =>
        server.GetIssues().Any(issue => issue.Key == CommunicationClient.ConnectionIssueKey);

    [Fact]
    public async Task Server_SelfHeals_AfterPortIsReleased()
    {
        var blocker = OccupyPort(out var port);
        var server = new AvCodersTcpServer(port, "test", CommandStringFormat.Ascii, _time);
        var connected = new ManualResetEventSlim();
        server.ConnectionStateHandlers += state =>
        {
            if (state == ConnectionState.Connected)
                connected.Set();
        };
        try
        {
            await Wait.UntilAsync(() => HasTheConnectionIssue(server), "no bind attempt failed");
            Assert.NotEqual(ConnectionState.Connected, server.ConnectionState);
            blocker.Stop();
            await TestNetwork.UntilAsync(_time, () => server.ConnectionState == ConnectionState.Disconnected,
                "the server never bound once the port was free");

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            await TestNetwork.UntilAsync(_time, () => connected.IsSet, "the server never accepted after rebinding");
        }
        finally
        {
            server.Disconnect();
        }
    }

    [Fact]
    public async Task BindFailure_RaisesTheConnectionIssue()
    {
        var blocker = OccupyPort(out var port);
        var server = new AvCodersTcpServer(port, "test", CommandStringFormat.Ascii, _time);
        try
        {
            await Wait.UntilAsync(() => HasTheConnectionIssue(server), "the failed bind raised no connection issue");
            var issue = server.GetIssues().Single(issue => issue.Key == CommunicationClient.ConnectionIssueKey);
            Assert.StartsWith($"The connection to Any:{port} failed", issue.Message);
            Assert.NotEqual(ConnectionState.Connected, server.ConnectionState);
            server.Send("x");
        }
        finally
        {
            server.Disconnect();
            blocker.Stop();
        }
    }
}
