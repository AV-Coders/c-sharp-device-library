using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using UdpClient = System.Net.Sockets.UdpClient;

namespace AVCoders.CommunicationClients.Tests;

public class AvCodersUdpClientTest : IDisposable
{
    private const string UnresolvableHost = "udp-test.invalid";
    private const int QueueTimeoutSeconds = 5;
    private static readonly TimeSpan AgeMargin = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SocketRetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(10);
    // Windows refuses a UDP connect to 0.0.0.0, Linux one to a broadcast address without SO_BROADCAST.
    // UdpClient turns SO_BROADCAST on itself for 255.255.255.255, so Linux uses the loopback broadcast.
    private static readonly IPAddress UnconnectableAddress = OperatingSystem.IsWindows() ? IPAddress.Any : IPAddress.Parse("127.255.255.255");
    private static readonly FieldInfo SocketField = typeof(AvCodersUdpClient).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo ReceiveMethod = typeof(AvCodersUdpClient).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly FakeTimeProvider _time = new();
    private readonly UdpClient _server;
    private readonly ushort _port;
    private IPAddress[]? _resolvesTo;
    private AvCodersUdpClient? _client;

    public AvCodersUdpClientTest()
    {
        _server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        _port = PortOf(_server);
    }

    private AvCodersUdpClient CreateClient(CommandStringFormat format = CommandStringFormat.Ascii) =>
        CreateClient("127.0.0.1", _port, format);

    private AvCodersUdpClient CreateClient(string host, ushort port, CommandStringFormat format = CommandStringFormat.Ascii)
    {
        _client = new AvCodersUdpClient(host, port, "TestUdpClient", format, _time);
        return _client;
    }

    private AvCodersUdpClient CreateFakeResolvedClient(ushort port)
    {
        _client = new AvCodersUdpClient(UnresolvableHost, port, "TestUdpClient", CommandStringFormat.Ascii, _time, Resolve);
        return _client;
    }

    private IPAddress[] Resolve(string host) => _resolvesTo ?? throw new SocketException((int)SocketError.HostNotFound);

    public void Dispose()
    {
        try { _client?.Disconnect(); } catch { }
        _server.Dispose();
    }

    private static ushort PortOf(UdpClient server) => (ushort)((IPEndPoint)server.Client.LocalEndPoint!).Port;

    private static UdpClient ServerFor(string host) => new(new IPEndPoint(Dns.GetHostAddresses(host)[0], 0));

    private Task<UdpReceiveResult> ReceiveAsync() => ReceiveAsync(_server);

    private Task<UdpReceiveResult> ReceiveAsync(UdpClient server) =>
        TestNetwork.AdvanceUntilDone(_time, server.ReceiveAsync(), "No datagram arrived");

    private async Task<string> ReceiveTextAsync() => Encoding.ASCII.GetString((await ReceiveAsync()).Buffer);

    private Task WaitForARetry(AvCodersUdpClient client)
    {
        var retried = false;
        client.ConnectionStateHandlers += state => retried |= state == ConnectionState.Connecting;
        return TestNetwork.UntilAsync(_time, () => retried, "the client never retried opening its socket");
    }

    private static UdpClient DisposedSocket()
    {
        var socket = new UdpClient();
        socket.Dispose();
        return socket;
    }

    [Fact]
    public async Task SendString_DeliversDatagramToServer()
    {
        var client = CreateClient();
        client.Send("udp hello");

        Assert.Equal("udp hello", await ReceiveTextAsync());
    }

    [Fact]
    public async Task SendBytes_DeliversDatagramToServer()
    {
        var client = CreateClient();
        var payload = new byte[] { 0x01, 0x02, 0xAA, 0xFF };
        client.Send(payload);

        var result = await ReceiveAsync();
        Assert.Equal(payload, result.Buffer);
    }

    [Fact]
    public async Task Receive_AsciiClient_InvokesStringAndByteResponseHandlers()
    {
        string? response = null;
        byte[]? responseBytes = null;
        var client = CreateClient();
        client.ResponseHandlers += text => response = text;
        client.ResponseByteHandlers += bytes => responseBytes = bytes;

        client.Send("ping");
        var request = await ReceiveAsync();
        var payload = Encoding.ASCII.GetBytes("SYSTEM STATUS = 0\r");
        await _server.SendAsync(payload, payload.Length, request.RemoteEndPoint);

        await Wait.UntilAsync(() => response != null && responseBytes != null, "the response handlers were never invoked");
        Assert.Equal("SYSTEM STATUS = 0\r", response);
        Assert.Equal(payload, responseBytes);
    }

    [Fact]
    public async Task Receive_HexClient_InvokesStringHandlersWithHexDump()
    {
        string? response = null;
        var client = CreateClient(CommandStringFormat.Hex);
        client.ResponseHandlers += text => response = text;

        client.Send("ping");
        var request = await ReceiveAsync();
        var payload = new byte[] { 0x01, 0xAA, 0xFF };
        await _server.SendAsync(payload, payload.Length, request.RemoteEndPoint);

        await Wait.UntilAsync(() => response != null, "string response handler never invoked");
        Assert.Equal(@"\x01 \xAA \xFF", response);
    }

    [Fact]
    public async Task Receive_AsciiClient_PassesBytesAbove0x7FThroughAsTheSameCharacters()
    {
        string? response = null;
        var client = CreateClient();
        client.ResponseHandlers += text => response = text;

        client.Send("ping");
        var request = await ReceiveAsync();
        var payload = new byte[] { 0xE9, 0x4F, 0x4B, 0x0D };
        await _server.SendAsync(payload, payload.Length, request.RemoteEndPoint);

        await Wait.UntilAsync(() => response != null, "string response handler never invoked");
        Assert.Equal("éOK\r", response);
    }

    [Fact]
    public async Task Receive_ADatagramArrivingAfterTheReceiveStarted_IsDeliveredByThatReceive()
    {
        string? response = null;
        var client = CreateClient();
        client.ResponseHandlers += text => response = text;
        client.Send("ping");
        var request = await ReceiveAsync();
        await TestNetwork.Workers(client)[0].Stop();

        var receive = (Task)ReceiveMethod.Invoke(client, [CancellationToken.None])!;
        Assert.False(receive.IsCompleted, "the receive finished before any datagram was sent");
        var payload = Encoding.ASCII.GetBytes("pong");
        await _server.SendAsync(payload, payload.Length, request.RemoteEndPoint);

        await receive.WaitAsync(ReceiveTimeout);
        Assert.Equal("pong", response);
    }

    [Fact]
    public async Task Send_WhenTheSocketFails_QueuesTheDatagramAndSendsItAfterReconnecting()
    {
        var client = CreateClient();
        client.SetQueueTimeout(60);
        using var originalSocket = (UdpClient)SocketField.GetValue(client)!;
        SocketField.SetValue(client, DisposedSocket());

        client.Send("after a failed send");

        Assert.Equal("after a failed send", await ReceiveTextAsync());
    }

    [Fact]
    public async Task Send_WhenAQueuedDatagramFails_SendsItAgainAheadOfTheDatagramsQueuedAfterIt()
    {
        var client = CreateClient();
        client.SetQueueTimeout(60);
        using var originalSocket = (UdpClient)SocketField.GetValue(client)!;
        SocketField.SetValue(client, null);
        client.Send("first");
        client.Send("second");

        SocketField.SetValue(client, DisposedSocket());

        var received = new List<string> { await ReceiveTextAsync(), await ReceiveTextAsync() };
        Assert.Equal(["first", "second"], received);
    }

    [Fact]
    public async Task Send_WhenAQueuedDatagramFails_GetsTheWholeQueueTimeoutAfterTheRebuild()
    {
        var client = CreateClient();
        client.SetQueueTimeout(QueueTimeoutSeconds);
        var sendQueueWorker = TestNetwork.SendQueueWorker(client);
        await sendQueueWorker.Stop();
        var sent = new ConcurrentQueue<string>();
        client.RequestHandlers += sent.Enqueue;
        using var originalSocket = (UdpClient)SocketField.GetValue(client)!;
        SocketField.SetValue(client, null);
        client.Send("requeued");
        client.Send("stale");
        await TestNetwork.Advance(_time, TimeSpan.FromSeconds(QueueTimeoutSeconds) - AgeMargin);
        SocketField.SetValue(client, DisposedSocket());

        await sendQueueWorker.Restart();

        Assert.Equal("requeued", await ReceiveTextAsync());
        await TestNetwork.UntilAsync(_time, () => TestNetwork.SendQueueCount(client) == 0,
            "the datagram queued behind it was never taken off the queue");
        Assert.Equal(["requeued"], sent);
    }

    [Fact]
    public async Task Send_WhenTheSocketFails_RebuildsTheSocketOffTheCallersThread()
    {
        var client = CreateClient();
        client.SetQueueTimeout(60);
        using var originalSocket = (UdpClient)SocketField.GetValue(client)!;
        SocketField.SetValue(client, DisposedSocket());
        var callerThread = Environment.CurrentManagedThreadId;
        var changesOnTheCallersThread = new ConcurrentQueue<ConnectionState>();
        client.ConnectionStateHandlers += state =>
        {
            if (Environment.CurrentManagedThreadId == callerThread)
                changesOnTheCallersThread.Enqueue(state);
        };

        client.Send("rebuilt elsewhere");

        Assert.Empty(changesOnTheCallersThread);
        Assert.Equal("rebuilt elsewhere", await ReceiveTextAsync());
    }

    [Fact]
    public async Task Receive_FromAPortThatIsClosed_RaisesTheConnectionIssueAndKeepsTheSocket()
    {
        var client = CreateClient("127.0.0.1", ClosedUdpPort());
        var socket = SocketField.GetValue(client);
        var states = new ConcurrentQueue<ConnectionState>();
        client.ConnectionStateHandlers += states.Enqueue;

        client.Send("ping");

        await Wait.UntilAsync(() => client.GetIssues().Any(i => i.Key == CommunicationClient.ConnectionIssueKey),
            "the closed port never raised the connection issue");
        Assert.Empty(states);
        Assert.Same(socket, SocketField.GetValue(client));
        Assert.Equal(ConnectionState.Connected, client.ConnectionState);
        Assert.Empty(client.Errors);
    }

    [Fact]
    public async Task Receive_AfterThePortWasClosed_ResolvesTheConnectionIssueWhenADatagramArrives()
    {
        var port = ClosedUdpPort();
        var client = CreateClient("127.0.0.1", port);
        client.ConnectionIssueThreshold = TimeSpan.Zero;
        client.Send("ping");
        await Wait.UntilAsync(() => client.GetOngoingIssues().Any(i => i.Key == CommunicationClient.ConnectionIssueKey),
            "the closed port never raised the ongoing connection issue");
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));

        client.Send("ping again");
        var request = await ReceiveAsync(server);
        var payload = Encoding.ASCII.GetBytes("pong");
        await server.SendAsync(payload, payload.Length, request.RemoteEndPoint);

        await Wait.UntilAsync(() => client.GetOngoingIssues().Count == 0, "the datagram never resolved the connection issue");
    }

    [Fact]
    public void Reconnect_OverlappingAnotherRebuild_LeavesOnlyOneSocketOpen()
    {
        var client = CreateClient();
        UdpClient? builtByTheOverlappingRebuild = null;
        var overlapped = false;
        client.ConnectionStateHandlers += state =>
        {
            if (state != ConnectionState.Connected || overlapped)
                return;
            overlapped = true;
            client.Reconnect();
            builtByTheOverlappingRebuild = (UdpClient?)SocketField.GetValue(client);
        };

        client.Reconnect();

        var kept = (UdpClient?)SocketField.GetValue(client);
        Assert.NotNull(builtByTheOverlappingRebuild);
        Assert.NotNull(kept);
        Assert.NotSame(builtByTheOverlappingRebuild, kept);
        Assert.Null(builtByTheOverlappingRebuild!.Client);
        Assert.NotNull(kept!.Client);
    }

    private static ushort ClosedUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return PortOf(probe);
    }

    [Fact]
    public async Task Send_AfterConnect_StillDeliversDatagram()
    {
        var client = CreateClient();
        client.Connect();

        client.Send("after reconnect");

        Assert.Equal("after reconnect", await ReceiveTextAsync());
    }

    [Fact]
    public async Task Send_WithoutASocket_IsQueuedThenSentWithItsTxAndTriggerIdOnceTheSocketIsBack()
    {
        var client = CreateClient();
        client.SetQueueTimeout(60);
        var sent = new ConcurrentQueue<(string Message, string? TriggerId)>();
        client.RequestHandlers += message => sent.Enqueue((message, Trigger.Id));
        using var originalSocket = (UdpClient)SocketField.GetValue(client)!;
        SocketField.SetValue(client, null);

        var triggerId = Trigger.Start();
        client.Send("queued datagram");
        Assert.Equal(1, TestNetwork.SendQueueCount(client));
        Assert.Empty(sent);

        client.Reconnect();

        Assert.Equal("queued datagram", await ReceiveTextAsync());
        await TestNetwork.UntilAsync(_time, () => !sent.IsEmpty, "the queued datagram never raised its tx");
        Assert.Equal(("queued datagram", triggerId), Assert.Single(sent));
    }

    [Fact]
    public async Task Send_WhileQueuedDatagramsAreWaiting_GoesOutAfterThem()
    {
        var client = CreateClient();
        client.SetQueueTimeout(60);
        using var originalSocket = (UdpClient)SocketField.GetValue(client)!;
        SocketField.SetValue(client, null);
        client.Send("first");
        client.Send("second");

        client.Reconnect();
        client.Send("third");

        var received = new List<string> { await ReceiveTextAsync(), await ReceiveTextAsync(), await ReceiveTextAsync() };
        Assert.Equal(["first", "second", "third"], received);
    }

    [Fact]
    public void Send_AfterDisconnect_IsDroppedWithoutThrowing()
    {
        var client = CreateClient();
        client.Disconnect();
        var requests = 0;
        client.RequestHandlers += _ => requests++;

        var exception = Record.Exception(() => client.Send("dropped while down"));

        Assert.Null(exception);
        Assert.Equal(0, requests);
        Assert.Equal(0, TestNetwork.SendQueueCount(client));
    }

    [Fact]
    public void Reconnect_AfterDisconnect_StaysDisconnected()
    {
        var client = CreateClient();
        client.Disconnect();

        client.Reconnect();

        Assert.Equal(ConnectionState.Disconnected, client.ConnectionState);
        Assert.Equal(ConnectionState.Disconnected, client.DesiredConnectionState);
    }

    [Fact]
    public async Task Connect_AfterDisconnect_DeliversDatagramsAgain()
    {
        var client = CreateClient();
        client.Disconnect();

        client.Connect();
        client.Send("after connect");

        Assert.Equal("after connect", await ReceiveTextAsync());
        Assert.Equal(ConnectionState.Connected, client.ConnectionState);
    }

    private AvCodersUdpClient CreateClientDisconnectedByAPerson()
    {
        var client = CreateClient();
        AsAPerson.Run(client.Disconnect);
        return client;
    }

    [Fact]
    public void Disconnect_ByAPerson_TurnsReconnectOnSendOn()
    {
        var client = CreateClientDisconnectedByAPerson();

        Assert.True(client.ReconnectOnSend);
        Assert.Equal(AsAPerson.Browser, client.DisconnectedBy);
    }

    [Fact]
    public async Task Send_ByAPersonAfterAPersonDisconnected_ReconnectsAndDeliversIt()
    {
        var client = CreateClientDisconnectedByAPerson();

        AsAPerson.Run(() => client.Send("power on"));

        Assert.Equal("power on", await ReceiveTextAsync());
        Assert.Equal(ConnectionState.Connected, client.DesiredConnectionState);
        Assert.Equal(ConnectionState.Connected, client.ConnectionState);
        Assert.False(client.ReconnectingForSend);
    }

    [Fact]
    public async Task Send_ByAPersonWhenTheSocketCannotOpen_WaitsUpToFifteenSeconds_WhileAPollKeepsTheQueueTimeout()
    {
        var client = CreateFakeResolvedClient(_port);
        AsAPerson.Run(client.Disconnect);

        AsAPerson.Run(() => client.Send("power on"));
        client.Send("poll");
        Assert.Equal(2, TestNetwork.SendQueueCount(client));
        await TestNetwork.Advance(_time, SocketRetryInterval + AgeMargin);
        _resolvesTo = [IPAddress.Loopback];

        Assert.Equal("power on", await ReceiveTextAsync());
        client.Send("end");
        Assert.Equal("end", await ReceiveTextAsync());
    }

    [Fact]
    public void Send_WithNoPersonAfterAPersonDisconnected_IsDropped()
    {
        var client = CreateClientDisconnectedByAPerson();

        client.Send("poll");

        Assert.Equal(ConnectionState.Disconnected, client.DesiredConnectionState);
        Assert.Equal(ConnectionState.Disconnected, client.ConnectionState);
        Assert.Equal(0, TestNetwork.SendQueueCount(client));
        Assert.True(client.ReconnectOnSend);
    }

    [Fact]
    public void Send_ByAPersonWithReconnectOnSendOff_IsDropped()
    {
        var client = CreateClientDisconnectedByAPerson();
        client.SetReconnectOnSend(false);

        AsAPerson.Run(() => client.Send("power on"));

        Assert.Equal(ConnectionState.Disconnected, client.DesiredConnectionState);
        Assert.Equal(ConnectionState.Disconnected, client.ConnectionState);
        Assert.Equal(0, TestNetwork.SendQueueCount(client));
    }

    [Fact]
    public async Task Receive_HostGivenAsAName_InvokesResponseHandlers()
    {
        using var server = ServerFor("localhost");
        string? response = null;
        var client = CreateClient("localhost", PortOf(server));
        client.ResponseHandlers += text => response = text;

        client.Send("ping");
        var request = await ReceiveAsync(server);
        var payload = Encoding.ASCII.GetBytes("pong");
        await server.SendAsync(payload, payload.Length, request.RemoteEndPoint);

        await Wait.UntilAsync(() => response != null, "a client given a host name never received");
        Assert.Equal("pong", response);
    }

    [Fact]
    public async Task HostWhoseFirstAddressCannotBeConnected_UsesTheNextAddress()
    {
        _resolvesTo = [UnconnectableAddress, IPAddress.Loopback];
        var client = CreateFakeResolvedClient(_port);

        client.Send("via the second address");

        Assert.Equal("via the second address", await ReceiveTextAsync());
        Assert.Equal(ConnectionState.Connected, client.ConnectionState);
        Assert.Empty(client.GetIssues());
    }

    [Fact]
    public void Constructor_HostThatDoesNotResolve_RaisesTheConnectionIssue()
    {
        var client = CreateFakeResolvedClient(_port);

        var issue = Assert.Single(client.GetIssues(), i => i.Key == CommunicationClient.ConnectionIssueKey);
        Assert.Contains(UnresolvableHost, issue.Message);
        Assert.Equal(ConnectionState.Disconnected, client.ConnectionState);
    }

    [Fact]
    public async Task Constructor_HostThatDoesNotResolve_LogsTheFailureOnceAcrossRetries()
    {
        var client = CreateFakeResolvedClient(_port);
        var retried = false;
        client.ConnectionStateHandlers += state => retried |= state == ConnectionState.Disconnected;

        await TestNetwork.UntilAsync(_time, () => retried, "the client never retried opening its socket");

        Assert.Empty(client.Errors);
        Assert.Single(client.Events, e => e.Type == EventType.Error);
    }

    [Fact]
    public async Task Constructor_HostThatDoesNotResolve_KeepsRetrying()
    {
        var client = CreateFakeResolvedClient(_port);

        await WaitForARetry(client);
    }

    [Fact]
    public async Task Connect_AfterDisconnect_HostThatDoesNotResolve_KeepsRetrying()
    {
        var client = CreateFakeResolvedClient(_port);
        client.Disconnect();

        client.Connect();

        await WaitForARetry(client);
    }

    [Fact]
    public async Task Disconnect_HostThatDoesNotResolve_StopsTheRetriesAndTheConnectionIssue()
    {
        var client = CreateFakeResolvedClient(_port);
        client.Disconnect();
        var attempts = 0;
        client.ConnectionStateHandlers += state => attempts += state == ConnectionState.Connecting ? 1 : 0;
        var issuesAtDisconnect = client.GetIssues();

        await TestNetwork.Advance(_time, SocketRetryInterval * 2);

        Assert.Equal(0, attempts);
        Assert.Equal(issuesAtDisconnect, client.GetIssues());
        Assert.All(TestNetwork.Workers(client), w => Assert.False(w.IsRunning));
    }

    [Fact]
    public async Task Disconnect_WhileARetryIsOpeningTheSocket_LeavesTheClientDisconnected()
    {
        var client = CreateFakeResolvedClient(_port);
        _resolvesTo = [IPAddress.Loopback];
        var disconnectRequested = false;
        client.ConnectionStateHandlers += state =>
        {
            if (state != ConnectionState.Connecting || disconnectRequested)
                return;
            disconnectRequested = true;
            client.Disconnect();
        };

        await TestNetwork.UntilAsync(_time, () => disconnectRequested, "the client never retried opening its socket");
        await Task.WhenAll(TestNetwork.Workers(client).Select(w => w.Stop()));

        Assert.Equal(ConnectionState.Disconnected, client.ConnectionState);
        Assert.Null(SocketField.GetValue(client));
    }

    [Fact]
    public async Task HostThatResolvesAfterStartup_Recovers_SendsADatagramQueuedWithinTheQueueTimeout_AndReceives()
    {
        string? response = null;
        var client = CreateFakeResolvedClient(_port);
        client.SetQueueTimeout(60);
        client.ResponseHandlers += text => response = text;
        client.Send("queued at startup");
        Assert.Equal(1, TestNetwork.SendQueueCount(client));

        _resolvesTo = [IPAddress.Loopback];

        var request = await ReceiveAsync();
        Assert.Equal("queued at startup", Encoding.ASCII.GetString(request.Buffer));
        Assert.Equal(ConnectionState.Connected, client.ConnectionState);
        var payload = Encoding.ASCII.GetBytes("pong");
        await _server.SendAsync(payload, payload.Length, request.RemoteEndPoint);
        await TestNetwork.UntilAsync(_time, () => response != null, "the recovered client never received");
        Assert.Equal("pong", response);
    }

    [Fact]
    public void Constructor_SetsTheDesiredConnectionStateToConnected()
    {
        var client = CreateClient();

        Assert.Equal(ConnectionState.Connected, client.DesiredConnectionState);
    }

    [Fact]
    public void Constructor_HostThatResolvesToNoAddresses_IsHostNotFoundWithTheHostName()
    {
        _resolvesTo = [];

        var client = CreateFakeResolvedClient(_port);

        var reason = client.ConnectionReason!;
        Assert.Equal(ConnectionReasonCategory.HostNotFound, reason.Category);
        Assert.Equal($"The host {UnresolvableHost} was not found", reason.Description);
        Assert.StartsWith("System.Net.Sockets.SocketException: ", reason.RawException);
        Assert.Equal(reason.Description, Assert.Single(client.GetIssues()).Message);
        Assert.Empty(client.Errors);
    }

    [Fact]
    public void Constructor_HostThatDoesNotResolve_IsHostNotFound()
    {
        var client = CreateFakeResolvedClient(_port);

        Assert.Equal(ConnectionReasonCategory.HostNotFound, client.ConnectionReason!.Category);
        Assert.Equal($"The host {UnresolvableHost} was not found", client.ConnectionReason.Description);
    }

    [Fact]
    public async Task Receive_FromAPortThatIsClosed_IsRefused_AndADatagramClearsIt()
    {
        var port = ClosedUdpPort();
        var client = CreateClient("127.0.0.1", port);
        client.Send("ping");
        await Wait.UntilAsync(() => client.ConnectionReason != null, "the closed port never set a reason");

        Assert.Equal(ConnectionReasonCategory.Refused, client.ConnectionReason!.Category);
        Assert.Equal(ConnectionState.Connected, client.ConnectionState);

        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        client.Send("ping again");
        var request = await ReceiveAsync(server);
        var payload = Encoding.ASCII.GetBytes("pong");
        await server.SendAsync(payload, payload.Length, request.RemoteEndPoint);
        await Wait.UntilAsync(() => client.ConnectionReason == null, "the datagram never cleared the reason");
    }

    [Fact]
    public void Disconnect_IsRequested()
    {
        var client = CreateFakeResolvedClient(_port);

        client.Disconnect();

        Assert.Equal(ConnectionReasonCategory.Requested, client.ConnectionReason!.Category);
    }
}
