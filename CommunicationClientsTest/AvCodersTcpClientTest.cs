using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AVCoders.CommunicationClients.Tests;

public class AvCodersTcpClientTest : IDisposable
{
    private readonly TcpListener _listener;
    private readonly ushort _port;
    private AvCodersTcpClient? _client;

    public AvCodersTcpClientTest()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _port = (ushort)((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    private AvCodersTcpClient CreateClient(ushort? port = null)
    {
        _client = new AvCodersTcpClient("127.0.0.1", port ?? _port, "TestTcpClient", CommandStringFormat.Ascii);
        return _client;
    }

    public void Dispose()
    {
        try { _client?.Disconnect(); } catch { /* test teardown */ }
        _listener.Stop();
    }

    [Fact]
    public async Task Client_ConnectsToServer_AndReportsConnected()
    {
        var states = new List<ConnectionState>();
        var client = CreateClient();
        client.ConnectionStateHandlers += state => states.Add(state);

        using var serverSide = await TestNetwork.AcceptAsync(_listener, 15);

        await TestNetwork.WaitUntilAsync(
            () => client.ConnectionState == ConnectionState.Connected,
            15, "client never reported Connected");
        Assert.Contains(ConnectionState.Connected, states);
    }

    [Fact]
    public async Task Send_DeliversBytesToServer()
    {
        string? request = null;
        var client = CreateClient();
        client.RequestHandlers += message => request = message;

        using var serverSide = await TestNetwork.AcceptAsync(_listener, 15);
        await TestNetwork.WaitUntilAsync(
            () => client.ConnectionState == ConnectionState.Connected,
            15, "client never connected");

        client.Send("hello device");

        var received = await TestNetwork.ReadStringAsync(serverSide, 15);
        Assert.Equal("hello device", received);
        Assert.Equal("hello device", request);
    }

    [Fact]
    public async Task Receive_InvokesStringAndByteResponseHandlers()
    {
        string? response = null;
        byte[]? responseBytes = null;
        var client = CreateClient();
        client.ResponseHandlers += message => response = message;
        client.ResponseByteHandlers += bytes => responseBytes = bytes;

        using var serverSide = await TestNetwork.AcceptAsync(_listener, 15);
        await TestNetwork.WaitUntilAsync(
            () => client.ConnectionState == ConnectionState.Connected,
            15, "client never connected");

        var payload = Encoding.ASCII.GetBytes("PONG\r");
        await serverSide.GetStream().WriteAsync(payload);

        await TestNetwork.WaitUntilAsync(() => response != null, 15, "response handler never invoked");
        Assert.Equal("PONG\r", response);
        Assert.Equal(payload, responseBytes);
    }

    [Fact]
    public void SetReceiveBufferSize_RejectsBuffersSmallerThan1024()
    {
        var client = CreateClient(TestNetwork.GetFreePort());
        Assert.Throws<ArgumentOutOfRangeException>(() => client.SetReceiveBufferSize(1023));
    }
}
