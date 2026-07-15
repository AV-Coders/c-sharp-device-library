using System.Net;
using System.Net.Sockets;
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

    private async Task<TcpClient> ConnectClientAsync()
    {
        // The server binds its listener on the ConnectionStateWorker shortly after
        // construction, so retry until the port accepts connections.
        var client = new TcpClient();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            try
            {
                await client.ConnectAsync("127.0.0.1", _port);
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
    public void Constructor_DoesNotThrow_WhenPortIsInUse()
    {
        var blocker = OccupyPort(out var port);
        try
        {
            var server = new AvCodersTcpServer(port, "test", CommandStringFormat.Ascii);
            Thread.Sleep(500); // Let the worker attempt (and fail) the bind
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
}
