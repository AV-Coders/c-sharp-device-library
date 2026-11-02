using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using UdpClient = System.Net.Sockets.UdpClient;

namespace AVCoders.CommunicationClients.Tests;

public class AvCodersMulticastClientTest
{
    private const string MulticastGroup = "239.255.10.10";
    private const string Ipv6MulticastGroup = "ff15::4156:4364";
    private const string HostName = "multicast-group.invalid";
    private const int Deaths = 3;
    private static readonly TimeSpan SecondBindBackoff = TimeSpan.FromMilliseconds(1800);
    private static readonly TimeSpan ThirdBindBackoff = TimeSpan.FromMilliseconds(3800);
    private static readonly TimeSpan UnboundReceiveWait = TimeSpan.FromSeconds(1);
    private readonly FakeTimeProvider _time = new();

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

    private AvCodersMulticastClient CreateClient(string group, ushort port, int ttl = 1, CommandStringFormat format = CommandStringFormat.Ascii) =>
        new(group, port, "test", ttl, format, _time);

    private static Socket OccupyUdpPort(out ushort port)
    {
        Socket blocker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
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
            var client = CreateClient(MulticastGroup, port);
            await Wait.UntilAsync(() => client.ConnectionState == ConnectionState.Error, "the client never attempted the bind");
            client.Send("x");
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
        var client = CreateClient(MulticastGroup, port);
        var connected = new ManualResetEventSlim();
        client.ConnectionStateHandlers += state =>
        {
            if (state == ConnectionState.Connected)
                connected.Set();
        };

        await Wait.UntilAsync(() => client.ConnectionState == ConnectionState.Error, "no bind attempt failed");
        blocker.Dispose();

        await TestNetwork.UntilAsync(_time, () => connected.IsSet, "the client never bound after the port was released");
        client.Send("x");
    }

    [Fact]
    public async Task Constructor_DoesNotThrow_GivenAHostName()
    {
        var client = CreateClient(HostName, 5000);
        try
        {
            await Wait.UntilAsync(
                () => client.GetIssues().Any(issue => issue.Key == CommunicationClient.ConnectionIssueKey)
                      && client.ConnectionState == ConnectionState.Error,
                "the host name raised no connection issue");
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
        var client = CreateClient(MulticastGroup, port);
        client.ConnectionIssueThreshold = TimeSpan.Zero;
        try
        {
            await Wait.UntilAsync(
                () => client.GetOngoingIssues().Any(issue => issue.Key == CommunicationClient.ConnectionIssueKey),
                "the failed bind raised no connection issue");
            Assert.Contains(client.GetIssues(), issue =>
                issue.Key == CommunicationClient.ConnectionIssueKey
                && issue.Message.StartsWith($"The connection to {MulticastGroup}:{port} failed"));

            blocker.Dispose();

            await TestNetwork.UntilAsync(_time, () => client.ConnectionState == ConnectionState.Connected,
                "the client never bound after the port was released");
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
        var client = CreateClient(MulticastGroup, port);
        var tx = new ConcurrentQueue<string>();
        client.RequestHandlers += message => tx.Enqueue(message);
        client.RequestByteHandlers += bytes => tx.Enqueue(BitConverter.ToString(bytes));
        try
        {
            await Wait.UntilAsync(() => client.ConnectionState == ConnectionState.Error, "the client never attempted the bind");

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
        var client = CreateClient(MulticastGroup, port);
        var payload = Enumerable.Range(0x80, 0x80).Select(value => (byte)value).ToArray();
        string? response = null;
        client.ResponseHandlers += message => response = message;
        try
        {
            await Wait.UntilAsync(() => client.ConnectionState == ConnectionState.Connected, "the client never bound");

            SendToGroup(MulticastGroup, port, payload);

            await TestNetwork.UntilAsync(_time, () => response != null, "the response handler was never invoked");
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
        var client = CreateClient(MulticastGroup, port);
        string? response = null;
        byte[]? received = null;
        client.ResponseHandlers += message => response = message;
        client.ResponseByteHandlers += bytes => received = bytes;
        try
        {
            await Wait.UntilAsync(() => client.ConnectionState == ConnectionState.Connected, "the client never bound");

            client.Send("\u0080caf\u00e9 \u00ff");

            await TestNetwork.UntilAsync(_time, () => received != null, "the client never received its own datagram");
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
        var client = CreateClient(MulticastGroup, port);
        var tx = new ConcurrentQueue<string>();
        byte[]? received = null;
        client.RequestHandlers += message => tx.Enqueue(message);
        client.ResponseByteHandlers += bytes => received = bytes;
        try
        {
            await Wait.UntilAsync(() => client.ConnectionState == ConnectionState.Connected, "the client never bound");

            client.Send("A\u20acB");

            await TestNetwork.UntilAsync(_time, () => received != null, "the client never received its own datagram");
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
        var client = CreateClient(MulticastGroup, port, format: CommandStringFormat.Hex);
        string? response = null;
        client.ResponseHandlers += message => response = message;
        try
        {
            await Wait.UntilAsync(() => client.ConnectionState == ConnectionState.Connected, "the client never bound");

            SendToGroup(MulticastGroup, port, [0x01, 0xAB]);

            await TestNetwork.UntilAsync(_time, () => response != null, "the response handler was never invoked");
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
    public async Task DeadSocket_ThatKeepsDying_IsReboundAtOnceThenWithTheGrowingBindBackoff()
    {
        var client = CreateClient(MulticastGroup, FreeUdpPort());
        var bindTimes = new ConcurrentQueue<DateTimeOffset>();
        client.ConnectionStateHandlers += state =>
        {
            if (state == ConnectionState.Connected)
                bindTimes.Enqueue(_time.GetUtcNow());
        };
        try
        {
            await Wait.UntilAsync(() => client.ConnectionState == ConnectionState.Connected, "the client never bound");
            await TestNetwork.Advance(_time, UnboundReceiveWait);
            var waits = new List<TimeSpan>();

            for (var death = 0; death < Deaths; death++)
            {
                var binds = bindTimes.Count;
                var diedAt = _time.GetUtcNow();
                BoundSocket(client)!.Dispose();
                if (death == 0)
                    await Wait.UntilAsync(() => bindTimes.Count > binds, "the first dead socket was not rebound at once");
                else
                    await TestNetwork.UntilAsync(_time, () => bindTimes.Count > binds, "the dead socket was never rebound");
                waits.Add(bindTimes.Last() - diedAt);
            }

            Assert.Equal(TimeSpan.Zero, waits[0]);
            Assert.True(waits[1] >= SecondBindBackoff, $"the second rebind waited only {waits[1]}");
            Assert.True(waits[2] >= ThirdBindBackoff, $"the third rebind waited only {waits[2]}");
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
        var client = CreateClient(MulticastGroup, port);
        var states = new ConcurrentQueue<ConnectionState>();
        client.ConnectionStateHandlers += state => states.Enqueue(state);
        try
        {
            await Wait.UntilAsync(() => client.ConnectionState == ConnectionState.Connected, "the client never bound");
            await TestNetwork.Advance(_time, UnboundReceiveWait);

            BoundSocket(client)!.Dispose();

            await Wait.UntilAsync(() => states.Contains(ConnectionState.Error), "the dead socket was never reported");
            await Wait.UntilAsync(() => client.ConnectionState == ConnectionState.Connected, "the dead socket was not rebound at once");
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
        var client = CreateClient(Ipv6MulticastGroup, port);
        string? response = null;
        client.ResponseHandlers += message => response = message;
        try
        {
            await Wait.UntilAsync(() => client.ConnectionState == ConnectionState.Connected, "the IPv6 group never bound");

            SendToGroup(Ipv6MulticastGroup, port, "hello"u8.ToArray());

            await TestNetwork.UntilAsync(_time, () => response != null, "the response handler was never invoked");
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
        var client = CreateClient(Ipv6MulticastGroup, FreeUdpPort(), 3);
        try
        {
            await Wait.UntilAsync(() => client.ConnectionState == ConnectionState.Connected, "the IPv6 group never bound");

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
    private static readonly Lazy<bool> HasAnIpv6MulticastInterface = new(FindIpv6MulticastInterface);

    public Ipv6MulticastFactAttribute()
    {
        if (!HasAnIpv6MulticastInterface.Value)
            Skip = "No network interface is up with multicast and a non-loopback IPv6 address";
    }

    private static bool FindIpv6MulticastInterface() =>
        NetworkInterface.GetAllNetworkInterfaces().Any(nic =>
            nic.OperationalStatus == OperationalStatus.Up
            && nic.SupportsMulticast
            && nic.Supports(NetworkInterfaceComponent.IPv6)
            && nic.GetIPProperties().UnicastAddresses.Any(unicast =>
                unicast.Address.AddressFamily == AddressFamily.InterNetworkV6 && !IPAddress.IsLoopback(unicast.Address)));
}
