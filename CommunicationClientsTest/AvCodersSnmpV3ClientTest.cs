using System.Net;
using System.Net.Sockets;

namespace AVCoders.CommunicationClients.Tests;

public class AvCodersSnmpV3ClientTest
{
    // A loopback port with nothing listening — every SNMP operation will fail with a
    // timeout or socket error. Before these were caught, the exception escaped into the
    // consuming driver's poll worker and killed it permanently.
    private readonly AvCodersSnmpV3Client _client =
        new("TestSnmp", "127.0.0.1", GetUnusedUdpPort(), "user", "authpass", "privpass");

    private static ushort GetUnusedUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return (ushort)((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    [Fact]
    public void Get_AgainstAnUnreachableDevice_ReturnsEmptyInsteadOfThrowing()
    {
        var result = _client.Get("1.3.6.1.2.1.1.1.0");

        Assert.Empty(result);
        Assert.Equal(ConnectionState.Error, _client.ConnectionState);
    }

    [Fact]
    public void Set_AgainstAnUnreachableDevice_ReturnsEmptyInsteadOfThrowing()
    {
        var result = _client.Set("1.3.6.1.2.1.1.5.0", "name");

        Assert.Empty(result);
        Assert.Equal(ConnectionState.Error, _client.ConnectionState);
    }

    [Fact]
    public void Walk_AgainstAnUnreachableDevice_ReturnsEmptyInsteadOfThrowing()
    {
        var result = _client.Walk("1.3.6.1.2.1.1");

        Assert.Empty(result);
        Assert.Equal(ConnectionState.Error, _client.ConnectionState);
    }

    [Fact]
    public void Constructor_WithAHostname_DoesNotThrow()
    {
        var exception = Record.Exception(() =>
            new AvCodersSnmpV3Client("TestSnmp", "pdu-rack1.local", 161, "user", "authpass", "privpass"));

        Assert.Null(exception);
    }

    [Fact]
    public void Get_WithAnUnresolvableHostname_ReturnsEmptyInsteadOfThrowing()
    {
        // .invalid is reserved (RFC 2606) and guaranteed never to resolve.
        var client = new AvCodersSnmpV3Client("TestSnmp", "device.invalid", 161, "user", "authpass", "privpass");

        var result = client.Get("1.3.6.1.2.1.1.1.0");

        Assert.Empty(result);
        Assert.Equal(ConnectionState.Error, client.ConnectionState);
    }
}
