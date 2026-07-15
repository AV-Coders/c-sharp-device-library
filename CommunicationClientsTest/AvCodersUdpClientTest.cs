using System.Net;
using System.Text;
using UdpClient = System.Net.Sockets.UdpClient;

namespace AVCoders.CommunicationClients.Tests;

public class AvCodersUdpClientTest : IDisposable
{
    private readonly UdpClient _server;
    private readonly ushort _port;
    private AvCodersUdpClient? _client;

    public AvCodersUdpClientTest()
    {
        _server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        _port = (ushort)((IPEndPoint)_server.Client.LocalEndPoint!).Port;
    }

    private AvCodersUdpClient CreateClient()
    {
        _client = new AvCodersUdpClient("127.0.0.1", _port, "TestUdpClient", CommandStringFormat.Ascii);
        return _client;
    }

    public void Dispose()
    {
        try { _client?.Disconnect(); } catch { /* test teardown */ }
        _server.Dispose();
    }

    private async Task<System.Net.Sockets.UdpReceiveResult> ReceiveAsync(int timeoutSeconds)
    {
        var receiveTask = _server.ReceiveAsync();
        var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
        Assert.True(completed == receiveTask, $"Timed out after {timeoutSeconds}s waiting for a datagram");
        return await receiveTask;
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
    public void Send_AfterDisconnect_QueuesWithoutThrowing()
    {
        var client = CreateClient();
        client.Disconnect();

        var exception = Record.Exception(() => client.Send("queued while down"));
        Assert.Null(exception);
    }
}
