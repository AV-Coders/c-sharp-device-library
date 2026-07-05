using System.Net;
using System.Net.Sockets;
using AVCoders.Core;
using TcpClient = System.Net.Sockets.TcpClient;

namespace AVCoders.CommunicationClients.Tests;

public class AvCodersTcpServerTest
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
