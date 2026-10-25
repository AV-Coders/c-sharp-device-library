using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using AVCoders.Core;
using UdpClient = System.Net.Sockets.UdpClient;

namespace AVCoders.CommunicationClients.Tests;

public class AvCodersMulticastClientTest
{
    private const string MulticastGroup = "239.255.10.10";
    internal const string Ipv6MulticastGroup = "ff15::4156:4364";
    private const string HostName = "multicast-group.invalid";

    private static ushort FreeUdpPort()
    {
        using var probe = OccupyUdpPort(out var port);
        return port;
    }

    private static async Task StopWorkers(AvCodersMulticastClient client)
    {
        foreach (var worker in TestNetwork.Workers(client))
            await worker.Stop();
    }

    private static UdpClient? BoundSocket(AvCodersMulticastClient client) =>
        (UdpClient?)typeof(AvCodersMulticastClient)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client);

    private static void SendToGroup(string group, ushort port, byte[] datagram)
    {
        var address = IPAddress.Parse(group);
        using var sender = new UdpClient(address.AddressFamily);
        sender.Send(datagram, datagram.Length, new IPEndPoint(address, port));
    }

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
    public async Task Constructor_DoesNotThrow_WhenPortIsInUse()
    {
        var blocker = OccupyUdpPort(out var port);
        try
        {
            var client = new AvCodersMulticastClient(MulticastGroup, port, "test");
            await Task.Delay(500); // Let the worker attempt (and fail) the bind
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

    [Fact]
    public async Task Constructor_DoesNotThrow_GivenAHostName()
    {
        var client = new AvCodersMulticastClient(HostName, 5000, "test");
        try
        {
            await TestNetwork.WaitUntilAsync(
                () => client.GetIssues().Any(issue => issue.Key == CommunicationClient.ConnectionIssueKey),
                10, "the host name raised no connection issue");
            var issue = client.GetIssues().Single(issue => issue.Key == CommunicationClient.ConnectionIssueKey);
            Assert.Equal($"The multicast group '{HostName}' is not an IP address, so it was not used", issue.Message);
            Assert.Equal(ConnectionState.Error, client.ConnectionState);
        }
        finally
        {
            await StopWorkers(client);
        }
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task BindFailure_RaisesTheConnectionIssue()
    {
        var blocker = OccupyUdpPort(out var port);
        var client = new AvCodersMulticastClient(MulticastGroup, port, "test");
        client.ConnectionIssueThreshold = TimeSpan.Zero;
        try
        {
            await TestNetwork.WaitUntilAsync(
                () => client.GetOngoingIssues().Any(issue => issue.Key == CommunicationClient.ConnectionIssueKey),
                10, "the failed bind raised no connection issue");
            Assert.Contains(client.GetIssues(), issue =>
                issue.Key == CommunicationClient.ConnectionIssueKey
                && issue.Message.StartsWith($"The connection to {MulticastGroup}:{port} failed"));

            blocker.Dispose();

            await TestNetwork.WaitUntilAsync(
                () => client.ConnectionState == ConnectionState.Connected,
                30, "the client never bound after the port was released");
            Assert.Empty(client.GetOngoingIssues());
        }
        finally
        {
            await StopWorkers(client);
            blocker.Dispose();
            BoundSocket(client)?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Send_WhileUnbound_InvokesNoRequestHandlers()
    {
        var blocker = OccupyUdpPort(out var port);
        var client = new AvCodersMulticastClient(MulticastGroup, port, "test");
        var tx = new ConcurrentQueue<string>();
        client.RequestHandlers += message => tx.Enqueue(message);
        client.RequestByteHandlers += bytes => tx.Enqueue(BitConverter.ToString(bytes));
        try
        {
            await TestNetwork.WaitUntilAsync(
                () => client.ConnectionState == ConnectionState.Error,
                10, "the client never attempted the bind");

            client.Send("dropped");
            client.Send([0x01, 0x02]);

            Assert.Empty(tx);
        }
        finally
        {
            await StopWorkers(client);
            blocker.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Receive_PassesBytesAbove0x7FThroughAsTheSameCharacters()
    {
        var port = FreeUdpPort();
        var client = new AvCodersMulticastClient(MulticastGroup, port, "test");
        var payload = Enumerable.Range(0x80, 0x80).Select(value => (byte)value).ToArray();
        string? response = null;
        client.ResponseHandlers += message => response = message;
        try
        {
            await TestNetwork.WaitUntilAsync(
                () => client.ConnectionState == ConnectionState.Connected,
                10, "the client never bound");

            SendToGroup(MulticastGroup, port, payload);

            await TestNetwork.WaitUntilAsync(() => response != null, 10, "the response handler was never invoked");
            Assert.Equal(new string(payload.Select(value => (char)value).ToArray()), response);
        }
        finally
        {
            await StopWorkers(client);
            BoundSocket(client)?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Text_RoundTripsThroughOneEncoding()
    {
        var port = FreeUdpPort();
        var client = new AvCodersMulticastClient(MulticastGroup, port, "test");
        string? response = null;
        byte[]? received = null;
        client.ResponseHandlers += message => response = message;
        client.ResponseByteHandlers += bytes => received = bytes;
        try
        {
            await TestNetwork.WaitUntilAsync(
                () => client.ConnectionState == ConnectionState.Connected,
                10, "the client never bound");

            client.Send("\u0080caf\u00e9 \u00ff");

            await TestNetwork.WaitUntilAsync(() => received != null, 10, "the client never received its own datagram");
            Assert.Equal(new byte[] { 0x80, 0x63, 0x61, 0x66, 0xE9, 0x20, 0xFF }, received);
            Assert.Equal("\u0080caf\u00e9 \u00ff", response);
        }
        finally
        {
            await StopWorkers(client);
            BoundSocket(client)?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Send_ReplacesCharactersAbove0xFFWithAQuestionMark()
    {
        var port = FreeUdpPort();
        var client = new AvCodersMulticastClient(MulticastGroup, port, "test");
        var tx = new ConcurrentQueue<string>();
        byte[]? received = null;
        client.RequestHandlers += message => tx.Enqueue(message);
        client.ResponseByteHandlers += bytes => received = bytes;
        try
        {
            await TestNetwork.WaitUntilAsync(
                () => client.ConnectionState == ConnectionState.Connected,
                10, "the client never bound");

            client.Send("A\u20acB");

            await TestNetwork.WaitUntilAsync(() => received != null, 10, "the client never received its own datagram");
            Assert.Equal(new byte[] { 0x41, 0x3F, 0x42 }, received);
            Assert.Equal(new[] { "A\u20acB" }, tx);
        }
        finally
        {
            await StopWorkers(client);
            BoundSocket(client)?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Receive_ForAHexClient_IsDumpedAsHex()
    {
        var port = FreeUdpPort();
        var client = new AvCodersMulticastClient(MulticastGroup, port, "test", 1, CommandStringFormat.Hex);
        string? response = null;
        client.ResponseHandlers += message => response = message;
        try
        {
            await TestNetwork.WaitUntilAsync(
                () => client.ConnectionState == ConnectionState.Connected,
                10, "the client never bound");

            SendToGroup(MulticastGroup, port, [0x01, 0xAB]);

            await TestNetwork.WaitUntilAsync(() => response != null, 10, "the response handler was never invoked");
            Assert.Equal("\\x01 \\xAB", response);
        }
        finally
        {
            await StopWorkers(client);
            BoundSocket(client)?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task DeadSocket_ThatKeepsDying_IsReboundWithTheBindBackoff()
    {
        var client = new AvCodersMulticastClient(MulticastGroup, FreeUdpPort(), "test");
        var binds = 0;
        client.ConnectionStateHandlers += state =>
        {
            if (state == ConnectionState.Connected)
                Interlocked.Increment(ref binds);
        };
        try
        {
            await TestNetwork.WaitUntilAsync(
                () => client.ConnectionState == ConnectionState.Connected,
                10, "the client never bound");

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                if (client.ConnectionState == ConnectionState.Connected)
                    BoundSocket(client)?.Dispose();
                await Task.Delay(50);
            }

            Assert.InRange(Volatile.Read(ref binds), 2, 5);
        }
        finally
        {
            await StopWorkers(client);
            BoundSocket(client)?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task DeadSocket_IsReboundAtOnce()
    {
        var port = FreeUdpPort();
        var client = new AvCodersMulticastClient(MulticastGroup, port, "test");
        var states = new ConcurrentQueue<ConnectionState>();
        client.ConnectionStateHandlers += state => states.Enqueue(state);
        try
        {
            await TestNetwork.WaitUntilAsync(
                () => client.ConnectionState == ConnectionState.Connected,
                10, "the client never bound");

            BoundSocket(client)!.Dispose();

            await TestNetwork.WaitUntilAsync(() => states.Contains(ConnectionState.Error), 10, "the dead socket was never reported");
            await TestNetwork.WaitUntilAsync(
                () => client.ConnectionState == ConnectionState.Connected,
                10, "the dead socket was not rebound at once");
            Assert.Contains(client.GetIssues(), issue => issue.Key == CommunicationClient.ConnectionIssueKey);
        }
        finally
        {
            await StopWorkers(client);
            BoundSocket(client)?.Dispose();
        }
    }

    [Ipv6MulticastFact]
    [Trait("Category", "Network")]
    public async Task Ipv6Group_BindsAndReceives()
    {
        var port = FreeUdpPort();
        var client = new AvCodersMulticastClient(Ipv6MulticastGroup, port, "test");
        string? response = null;
        client.ResponseHandlers += message => response = message;
        try
        {
            await TestNetwork.WaitUntilAsync(
                () => client.ConnectionState == ConnectionState.Connected,
                10, "the IPv6 group never bound");

            SendToGroup(Ipv6MulticastGroup, port, "hello"u8.ToArray());

            await TestNetwork.WaitUntilAsync(() => response != null, 10, "the response handler was never invoked");
            Assert.Equal("hello", response);
        }
        finally
        {
            await StopWorkers(client);
            BoundSocket(client)?.Dispose();
        }
    }

    [Ipv6MulticastFact]
    [Trait("Category", "Network")]
    public async Task Ipv6Group_SetsTheTtlAsTheMulticastHopLimit()
    {
        var client = new AvCodersMulticastClient(Ipv6MulticastGroup, FreeUdpPort(), "test", 3);
        try
        {
            await TestNetwork.WaitUntilAsync(
                () => client.ConnectionState == ConnectionState.Connected,
                10, "the IPv6 group never bound");

            var hops = BoundSocket(client)!.Client.GetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive);

            Assert.Equal(3, hops);
        }
        finally
        {
            await StopWorkers(client);
            BoundSocket(client)?.Dispose();
        }
    }
}

public sealed class Ipv6MulticastFactAttribute : FactAttribute
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly Lazy<string?> Unsupported = new(FindIpv6MulticastProblem);

    public Ipv6MulticastFactAttribute()
    {
        Skip = Unsupported.Value;
    }

    private static string? FindIpv6MulticastProblem()
    {
        var hasInterface = NetworkInterface.GetAllNetworkInterfaces().Any(nic =>
            nic.OperationalStatus == OperationalStatus.Up
            && nic.SupportsMulticast
            && nic.Supports(NetworkInterfaceComponent.IPv6)
            && nic.GetIPProperties().UnicastAddresses.Any(unicast =>
                unicast.Address.AddressFamily == AddressFamily.InterNetworkV6 && !IPAddress.IsLoopback(unicast.Address)));
        if (!hasInterface)
            return "No network interface is up with multicast and a non-loopback IPv6 address";

        var group = IPAddress.Parse(AvCodersMulticastClientTest.Ipv6MulticastGroup);
        try
        {
            using var probe = new UdpClient(AddressFamily.InterNetworkV6);
            probe.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
            probe.JoinMulticastGroup(group);
            var port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
            probe.Send([0x01], 1, new IPEndPoint(group, port));
            return probe.ReceiveAsync().Wait(ProbeTimeout)
                ? null
                : $"A datagram sent to {group} didn't come back within {ProbeTimeout.TotalSeconds} s";
        }
        catch (Exception e) when (e is SocketException or AggregateException)
        {
            return $"This machine can't join or send to {group}: {e.Message}";
        }
    }
}
