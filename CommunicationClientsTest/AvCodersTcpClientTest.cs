using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

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

[Collection(LoggerFactoryCollection.Name)]
public class AvCodersTcpClientMaskTest : IDisposable
{
    private const string ClientName = "MaskTestClient";
    private const string Password = "s3cret-pw";
    private const string Secret = "\"Password\":\"s3cret-pw\"";
    private const string Shown = "\"Password\":\"***\"";
    private const string Logon = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"Logon\",\"params\":{\"User\":\"tech\",\"Password\":\"s3cret-pw\"}}\0";
    private const string ShownLogon = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"Logon\",\"params\":{\"User\":\"tech\",\"Password\":\"***\"}}\0";
    private readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private AvCodersTcpClient? _client;

    public AvCodersTcpClientMaskTest()
    {
        LogBase.LoggerFactory = _logs;
        _listener.Start();
    }

    public void Dispose()
    {
        try { _client?.Disconnect(); } catch { }
        _listener.Stop();
        LogBase.LoggerFactory = NullLoggerFactory.Instance;
        _logs.Dispose();
    }

    [Theory]
    [InlineData(CommandStringFormat.Ascii)]
    [InlineData(CommandStringFormat.Hex)]
    public async Task AMaskedText_GoesToTheDevice_ButTheVerboseLogAndTheRequestHandlersShowItsMask(CommandStringFormat format)
    {
        _client = new AvCodersTcpClient("127.0.0.1", (ushort)((IPEndPoint)_listener.LocalEndpoint).Port, ClientName, format);
        _client.MaskInRequests(Secret, Shown);
        var requests = new ConcurrentQueue<string>();
        var requestBytes = new ConcurrentQueue<byte[]>();
        _client.RequestHandlers += requests.Enqueue;
        _client.RequestByteHandlers += requestBytes.Enqueue;
        using var serverSide = await TestNetwork.AcceptAsync(_listener, 15);
        await Wait.UntilAsync(() => _client.ConnectionState == ConnectionState.Connected, "client never connected");

        _client.Send(Logon);

        Assert.Equal(Logon, await TestNetwork.ReadStringAsync(serverSide, 15));
        await Wait.UntilAsync(() => !requests.IsEmpty && !requestBytes.IsEmpty, "the request handlers never ran");
        Assert.Equal(ShownLogon, Assert.Single(requests));
        Assert.Equal(Encoding.Latin1.GetBytes(ShownLogon), Assert.Single(requestBytes));
        Assert.Equal(2, _logs.Logs.Count(log => Equals(log.Scope.GetValueOrDefault("InstanceName"), ClientName) && log.Message.StartsWith("Sending")));
        var passwordHex = BitConverter.ToString(Encoding.ASCII.GetBytes(Password));
        Assert.DoesNotContain(_logs.Logs, log => log.Message.Contains(Password) || log.Message.Contains(passwordHex));
    }
}
