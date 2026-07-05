using System.Net;
using System.Net.Sockets;
using AVCoders.Core;

namespace AVCoders.CommunicationClients.Tests;

public class AvCodersMulticastClientTest
{
    private const string MulticastGroup = "239.255.10.10";

    private static Socket OccupyUdpPort(out ushort port)
    {
        Socket blocker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Exclusive use so the client's ReuseAddress bind genuinely fails while we hold the port
        blocker.ExclusiveAddressUse = true;
        blocker.Bind(new IPEndPoint(IPAddress.Any, 0));
        port = (ushort)((IPEndPoint)blocker.LocalEndPoint!).Port;
        return blocker;
    }

    [Fact]
    [Trait("Category", "Network")]
    public void Constructor_DoesNotThrow_WhenPortIsInUse()
    {
        var blocker = OccupyUdpPort(out var port);
        try
        {
            var client = new AvCodersMulticastClient(MulticastGroup, port, "test");
            Thread.Sleep(500); // Let the worker attempt (and fail) the bind
            Assert.NotEqual(ConnectionState.Connected, client.ConnectionState);
            client.Send("x"); // Must not throw while unbound
        }
        finally
        {
            blocker.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Client_SelfHeals_AfterPortIsReleased()
    {
        var blocker = OccupyUdpPort(out var port);
        var client = new AvCodersMulticastClient(MulticastGroup, port, "test");
        var connected = new ManualResetEventSlim();
        client.ConnectionStateHandlers += state =>
        {
            if (state == ConnectionState.Connected)
                connected.Set();
        };

        await Task.Delay(1500); // Let at least one bind attempt fail
        Assert.NotEqual(ConnectionState.Connected, client.ConnectionState);
        blocker.Dispose();

        Assert.True(connected.Wait(TimeSpan.FromSeconds(30)));
        client.Send("x"); // Must not throw once bound
    }
}
