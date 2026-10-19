using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using UdpClient = System.Net.Sockets.UdpClient;

namespace AVCoders.CommunicationClients.Tests;

public class AvCodersUdpClientTest : IDisposable
{
    private const string UnresolvableHost = "udp-test.invalid";
    private const int RetryWaitSeconds = 10;
    private static readonly PropertyInfo HostProperty = typeof(CommunicationClient).GetProperty(nameof(CommunicationClient.Host))!;
    private static readonly FieldInfo SocketField = typeof(AvCodersUdpClient).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly TimeSpan SocketRetryInterval = (TimeSpan)typeof(AvCodersUdpClient)
        .GetField("SocketRetryInterval", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    private readonly UdpClient _server;
    private readonly ushort _port;
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
        _client = new AvCodersUdpClient(host, port, "TestUdpClient", format);
        return _client;
    }

    public void Dispose()
    {
        try { _client?.Disconnect(); } catch { /* test teardown */ }
        _server.Dispose();
    }

    private static ushort PortOf(UdpClient server) => (ushort)((IPEndPoint)server.Client.LocalEndPoint!).Port;

    private static UdpClient ServerFor(string host) => new(new IPEndPoint(Dns.GetHostAddresses(host)[0], 0));

    private Task<System.Net.Sockets.UdpReceiveResult> ReceiveAsync(int timeoutSeconds) => ReceiveAsync(_server, timeoutSeconds);

    private static async Task<System.Net.Sockets.UdpReceiveResult> ReceiveAsync(UdpClient server, int timeoutSeconds)
    {
        var receiveTask = server.ReceiveAsync();
        var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
        Assert.True(completed == receiveTask, $"Timed out after {timeoutSeconds}s waiting for a datagram");
        return await receiveTask;
    }

    private static async Task WaitForARetry(AvCodersUdpClient client)
    {
        var retried = false;
        client.ConnectionStateHandlers += state => retried |= state == ConnectionState.Connecting;
        await TestNetwork.WaitUntilAsync(() => retried, RetryWaitSeconds, "the client never retried opening its socket");
    }

    [Fact]
    public async Task SendString_DeliversDatagramToServer()
    {
        var client = CreateClient();
        client.Send("udp hello");

        var result = await ReceiveAsync(10);
        Assert.Equal("udp hello", Encoding.ASCII.GetString(result.Buffer));
    }

    [Fact]
    public async Task SendBytes_DeliversDatagramToServer()
    {
        var client = CreateClient();
        var payload = new byte[] { 0x01, 0x02, 0xAA, 0xFF };
        client.Send(payload);

        var result = await ReceiveAsync(10);
        Assert.Equal(payload, result.Buffer);
    }

    [Fact]
    public async Task Receive_InvokesByteResponseHandlers()
    {
        byte[]? responseBytes = null;
        var client = CreateClient();
        client.ResponseByteHandlers += bytes => responseBytes = bytes;

        // Learn the client's ephemeral endpoint from its first datagram, then reply.
        client.Send("ping");
        var request = await ReceiveAsync(10);
        var payload = Encoding.ASCII.GetBytes("pong");
        await _server.SendAsync(payload, payload.Length, request.RemoteEndPoint);

        await TestNetwork.WaitUntilAsync(() => responseBytes != null, 15, "byte response handler never invoked");
        Assert.Equal(payload, responseBytes);
    }

    [Fact]
    public async Task Receive_AsciiClient_InvokesStringHandlersWithDecodedText()
    {
        string? response = null;
        var client = CreateClient();
        client.ResponseHandlers += text => response = text;

        client.Send("ping");
        var request = await ReceiveAsync(10);
        var payload = Encoding.ASCII.GetBytes("SYSTEM STATUS = 0\r");
        await _server.SendAsync(payload, payload.Length, request.RemoteEndPoint);

        await TestNetwork.WaitUntilAsync(() => response != null, 15, "string response handler never invoked");
        Assert.Equal("SYSTEM STATUS = 0\r", response);
    }

    [Fact]
    public async Task Receive_HexClient_InvokesStringHandlersWithHexDump()
    {
        string? response = null;
        var client = CreateClient(CommandStringFormat.Hex);
        client.ResponseHandlers += text => response = text;

        client.Send("ping");
        var request = await ReceiveAsync(10);
        var payload = new byte[] { 0x01, 0xAA, 0xFF };
        await _server.SendAsync(payload, payload.Length, request.RemoteEndPoint);

        await TestNetwork.WaitUntilAsync(() => response != null, 15, "string response handler never invoked");
        Assert.Equal(@"\x01 \xAA \xFF", response);
    }

    [Fact]
    public async Task Send_AfterConnect_StillDeliversDatagram()
    {
        var client = CreateClient();
        // Connect() reconnects: it closes the old socket and must swap the new one into place,
        // otherwise sends keep going to the disposed socket and are silently lost.
        client.Connect();

        client.Send("after reconnect");

        var result = await ReceiveAsync(10);
        Assert.Equal("after reconnect", Encoding.ASCII.GetString(result.Buffer));
    }

    [Fact]
    public async Task Send_WithoutASocket_IsQueuedThenSentWithItsTxAndTriggerIdOnceTheSocketIsBack()
    {
        var client = CreateClient();
        client.SetQueueTimeout(60);
        var sent = new ConcurrentQueue<(string Message, string? TriggerId)>();
        client.RequestHandlers += message => sent.Enqueue((message, Trigger.Id));
        var socketField = typeof(AvCodersUdpClient).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var originalSocket = (UdpClient)socketField.GetValue(client)!;
        socketField.SetValue(client, null);

        var triggerId = Trigger.Start();
        client.Send("queued datagram");
        Assert.Equal(1, TestNetwork.SendQueueCount(client));
        Assert.Empty(sent);

        client.Reconnect();

        var result = await ReceiveAsync(10);
        Assert.Equal("queued datagram", Encoding.ASCII.GetString(result.Buffer));
        await TestNetwork.WaitUntilAsync(() => !sent.IsEmpty, 10, "the queued datagram never raised its tx");
        Assert.Equal(("queued datagram", triggerId), Assert.Single(sent));
    }

    [Fact]
    public async Task Send_WhileQueuedDatagramsAreWaiting_GoesOutAfterThem()
    {
        var client = CreateClient();
        client.SetQueueTimeout(60);
        var socketField = typeof(AvCodersUdpClient).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var originalSocket = (UdpClient)socketField.GetValue(client)!;
        socketField.SetValue(client, null);
        client.Send("first");
        client.Send("second");

        client.Reconnect();
        client.Send("third");

        var received = new List<string>();
        for (var i = 0; i < 3; i++)
            received.Add(Encoding.ASCII.GetString((await ReceiveAsync(10)).Buffer));
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

        var result = await ReceiveAsync(10);
        Assert.Equal("after connect", Encoding.ASCII.GetString(result.Buffer));
        Assert.Equal(ConnectionState.Connected, client.ConnectionState);
    }

    [Fact]
    public async Task Receive_HostGivenAsAName_InvokesResponseHandlers()
    {
        using var server = ServerFor("localhost");
        string? response = null;
        var client = CreateClient("localhost", PortOf(server));
        client.ResponseHandlers += text => response = text;

        client.Send("ping");
        var request = await ReceiveAsync(server, 10);
        var payload = Encoding.ASCII.GetBytes("pong");
        await server.SendAsync(payload, payload.Length, request.RemoteEndPoint);

        await TestNetwork.WaitUntilAsync(() => response != null, 10, "a client given a host name never received");
        Assert.Equal("pong", response);
    }

    [Fact]
    public void Constructor_HostThatDoesNotResolve_RaisesTheConnectionIssue()
    {
        var client = CreateClient(UnresolvableHost, _port);

        var issue = Assert.Single(client.GetIssues(), i => i.Key == CommunicationClient.ConnectionIssueKey);
        Assert.Contains(UnresolvableHost, issue.Message);
        Assert.Equal(ConnectionState.Disconnected, client.ConnectionState);
    }

    [Fact]
    public async Task Constructor_HostThatDoesNotResolve_KeepsRetrying()
    {
        var client = CreateClient(UnresolvableHost, _port);

        await WaitForARetry(client);
    }

    [Fact]
    public async Task Connect_AfterDisconnect_HostThatDoesNotResolve_KeepsRetrying()
    {
        var client = CreateClient(UnresolvableHost, _port);
        client.Disconnect();

        client.Connect();

        await WaitForARetry(client);
    }

    [Fact]
    public async Task Disconnect_HostThatDoesNotResolve_StopsTheRetriesAndTheConnectionIssue()
    {
        var client = CreateClient(UnresolvableHost, _port);
        client.Disconnect();
        var attempts = 0;
        client.ConnectionStateHandlers += state => attempts += state == ConnectionState.Connecting ? 1 : 0;
        var issuesAtDisconnect = client.GetIssues();

        await Task.Delay(SocketRetryInterval + TimeSpan.FromSeconds(1));

        Assert.Equal(0, attempts);
        Assert.Equal(issuesAtDisconnect, client.GetIssues());
        Assert.All(TestNetwork.Workers(client), w => Assert.False(w.IsRunning));
    }

    [Fact]
    public async Task Disconnect_WhileARetryIsOpeningTheSocket_LeavesTheClientDisconnected()
    {
        var client = CreateClient(UnresolvableHost, _port);
        HostProperty.SetValue(client, "127.0.0.1");
        var disconnectRequested = false;
        client.ConnectionStateHandlers += state =>
        {
            if (state != ConnectionState.Connecting || disconnectRequested)
                return;
            disconnectRequested = true;
            client.Disconnect();
        };

        await TestNetwork.WaitUntilAsync(() => disconnectRequested, RetryWaitSeconds, "the client never retried opening its socket");
        await Task.WhenAll(TestNetwork.Workers(client).Select(w => w.Stop()));

        Assert.Equal(ConnectionState.Disconnected, client.ConnectionState);
        Assert.Null(SocketField.GetValue(client));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task HostThatResolvesAfterStartup_Recovers_SendsADatagramQueuedWithinTheQueueTimeout_AndReceives(string laterHost)
    {
        using var server = ServerFor(laterHost);
        string? response = null;
        var client = CreateClient(UnresolvableHost, PortOf(server));
        client.SetQueueTimeout(60);
        client.ResponseHandlers += text => response = text;
        client.Send("queued at startup");
        Assert.Equal(1, TestNetwork.SendQueueCount(client));

        HostProperty.SetValue(client, laterHost);

        var request = await ReceiveAsync(server, RetryWaitSeconds);
        Assert.Equal("queued at startup", Encoding.ASCII.GetString(request.Buffer));
        Assert.Equal(ConnectionState.Connected, client.ConnectionState);
        var payload = Encoding.ASCII.GetBytes("pong");
        await server.SendAsync(payload, payload.Length, request.RemoteEndPoint);
        await TestNetwork.WaitUntilAsync(() => response != null, 10, "the recovered client never received");
        Assert.Equal("pong", response);
    }
}
