using System.Collections.Concurrent;
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

    private AvCodersTcpClient CreateClient(ushort? port = null, CommandStringFormat format = CommandStringFormat.Ascii)
    {
        _client = new AvCodersTcpClient("127.0.0.1", port ?? _port, "TestTcpClient", format);
        return _client;
    }

    private async Task<System.Net.Sockets.TcpClient> AcceptConnectedAsync(AvCodersTcpClient client)
    {
        var serverSide = await TestNetwork.AcceptAsync(_listener, 15);
        await Wait.UntilAsync(() => client.ConnectionState == ConnectionState.Connected, "client never connected");
        return serverSide;
    }

    public void Dispose()
    {
        try { _client?.Disconnect(); } catch { }
        _listener.Stop();
    }

    [Fact]
    public async Task Client_ConnectsToServer_AndReportsConnected()
    {
        var port = TestNetwork.GetFreePort();
        var states = new ConcurrentQueue<ConnectionState>();
        var client = CreateClient(port);
        client.ConnectionStateHandlers += state => states.Enqueue(state);

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            using var serverSide = await TestNetwork.AcceptAsync(listener, 15);

            await Wait.UntilAsync(() => states.Contains(ConnectionState.Connected), "client never reported Connected");
            Assert.Equal(ConnectionState.Connected, client.ConnectionState);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Send_DeliversBytesToServer()
    {
        string? request = null;
        var client = CreateClient();
        client.RequestHandlers += message => request = message;
        using var serverSide = await AcceptConnectedAsync(client);

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
        using var serverSide = await AcceptConnectedAsync(client);

        var payload = Encoding.ASCII.GetBytes("PONG\r");
        await serverSide.GetStream().WriteAsync(payload);

        await Wait.UntilAsync(() => responseBytes != null, "byte response handler never invoked");
        Assert.Equal("PONG\r", response);
        Assert.Equal(payload, responseBytes);
    }

    [Fact]
    public async Task Receive_AsciiClient_PassesBytesAbove0x7FThroughAsTheSameCharacters()
    {
        string? response = null;
        var client = CreateClient();
        client.ResponseHandlers += message => response = message;
        using var serverSide = await AcceptConnectedAsync(client);

        await serverSide.GetStream().WriteAsync(new byte[] { 0xE9, 0x4F, 0x4B, 0x0D });

        await Wait.UntilAsync(() => response != null, "string response handler never invoked");
        Assert.Equal("éOK\r", response);
    }

    [Fact]
    public async Task Receive_HexClient_InvokesStringHandlersWithHexDump()
    {
        string? response = null;
        byte[]? responseBytes = null;
        var client = CreateClient(format: CommandStringFormat.Hex);
        client.ResponseHandlers += message => response = message;
        client.ResponseByteHandlers += bytes => responseBytes = bytes;
        using var serverSide = await AcceptConnectedAsync(client);
        var payload = new byte[] { 0x01, 0xAA, 0xFF };

        await serverSide.GetStream().WriteAsync(payload);

        await Wait.UntilAsync(() => responseBytes != null, "byte response handler never invoked");
        Assert.Equal(@"\x01 \xAA \xFF", response);
        Assert.Equal(payload, responseBytes);
    }

    [Fact]
    public async Task Send_PassesCharactersUpTo0xFFThroughAsTheSameByte()
    {
        var client = CreateClient();
        using var serverSide = await AcceptConnectedAsync(client);

        client.Send("éOK\r");

        var buffer = new byte[16];
        var read = serverSide.GetStream().ReadAsync(buffer, 0, buffer.Length);
        Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(15))));
        Assert.Equal(new byte[] { 0xE9, 0x4F, 0x4B, 0x0D }, buffer[..await read]);
    }
}
