using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using MQTTnet;

namespace AVCoders.CommunicationClients.Tests;

public class AvCodersMqttClientTest : IDisposable
{
    private const byte ConnectPacket = 0x10;
    private const byte SubscribePacket = 0x82;
    private const byte PingRequestPacket = 0xC0;
    private const byte PublishPacket = 0x30;
    private const byte SubscribeAckPacket = 0x90;
    private const byte GrantedQosOne = 0x01;
    private const byte NoProperties = 0x00;
    private const string Topic = "room/status";
    private const int MaxAttemptsInWindow = 5;
    private const int SendsDuringOutage = 5;
    private static readonly byte[] ConnectAck = [0x20, 0x03, 0x00, 0x00, NoProperties];
    private static readonly byte[] BadCredentialsConnectAck = [0x20, 0x03, 0x00, 0x86, NoProperties];
    private static readonly byte[] PingResponse = [0xD0, 0x00];
    private static readonly TimeSpan ObservationWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SendSpacing = TimeSpan.FromMilliseconds(600);
    private static readonly Actor Sender = new(ActorKind.Browser, "10.0.0.1", "Test agent", null, "connection-1");

    private readonly ushort _port = TestNetwork.GetFreePort();
    private readonly List<AvCodersMqttClient> _clients = new();
    private readonly object _receivedLock = new();
    private bool _received;
    private Actor? _receivedActor;
    private string? _receivedTriggerId;
    private int _connectAttempts;
    private int _failuresLogged;

    public void Dispose()
    {
        var mqttClientField = typeof(AvCodersMqttClient).GetField("_mqttClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var client in _clients)
            ((IMqttClient)mqttClientField.GetValue(client)!).Dispose();
    }

    private static async Task<(byte Type, byte[] Body)> ReadPacketAsync(NetworkStream stream)
    {
        var header = new byte[1];
        await stream.ReadExactlyAsync(header);
        int length = 0;
        int multiplier = 1;
        var lengthByte = new byte[1];
        do
        {
            await stream.ReadExactlyAsync(lengthByte);
            length += (lengthByte[0] & 0x7F) * multiplier;
            multiplier *= 128;
        } while ((lengthByte[0] & 0x80) != 0);
        var body = new byte[length];
        await stream.ReadExactlyAsync(body);
        return (header[0], body);
    }

    private static byte[] BuildPublish(string payload)
    {
        byte[] topic = Encoding.UTF8.GetBytes(Topic);
        byte[] body = Encoding.UTF8.GetBytes(payload);
        int remainingLength = 2 + topic.Length + 1 + body.Length;
        return [PublishPacket, (byte)remainingLength, 0x00, (byte)topic.Length, .. topic, NoProperties, .. body];
    }

    private static async Task ServeSessionAsync(System.Net.Sockets.TcpClient connection)
    {
        var stream = connection.GetStream();
        while (true)
        {
            var (type, body) = await ReadPacketAsync(stream);
            switch (type)
            {
                case ConnectPacket:
                    await stream.WriteAsync(ConnectAck);
                    break;
                case SubscribePacket:
                    await stream.WriteAsync(new byte[] { SubscribeAckPacket, 0x04, body[0], body[1], NoProperties, GrantedQosOne });
                    await stream.WriteAsync(BuildPublish("on"));
                    break;
                case PingRequestPacket:
                    await stream.WriteAsync(PingResponse);
                    break;
            }
        }
    }

    private async Task DropEveryConnectionAsync(TcpListener listener)
    {
        try
        {
            while (true)
            {
                using var connection = await listener.AcceptTcpClientAsync();
                Interlocked.Increment(ref _connectAttempts);
            }
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task RejectEveryConnectAsync(TcpListener listener)
    {
        try
        {
            while (true)
            {
                using var connection = await listener.AcceptTcpClientAsync();
                await ReadPacketAsync(connection.GetStream());
                await connection.GetStream().WriteAsync(BadCredentialsConnectAck);
            }
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private AvCodersMqttClient CreateClient(string? username = null, string? password = null)
    {
        var client = new AvCodersMqttClient("127.0.0.1", _port, username, password, "TestMqttClient");
        client.ErrorsUpdated += () => Interlocked.Increment(ref _failuresLogged);
        _clients.Add(client);
        return client;
    }

    private void RecordReceived(string payload)
    {
        lock (_receivedLock)
        {
            _receivedActor = Actor.Current;
            _receivedTriggerId = Trigger.Id;
            _received = true;
        }
    }

    [Fact]
    public async Task MessageAfterAFailedSend_DoesNotCarryTheSendersActorAndHasATriggerId()
    {
        Actor.Current = Sender;
        var senderTriggerId = Trigger.Start();
        var client = CreateClient();
        client.SubscribeToTopic(Topic, RecordReceived);
        client.Send(Topic, "command");

        var listener = new TcpListener(IPAddress.Loopback, _port);
        listener.Start();
        using var connection = await TestNetwork.AcceptAsync(listener, 20);
        _ = ServeSessionAsync(connection);
        await TestNetwork.WaitUntilAsync(() => { lock (_receivedLock) return _received; }, 20,
            "The subscribed message never arrived");
        listener.Stop();

        Assert.Null(_receivedActor);
        Assert.NotNull(_receivedTriggerId);
        Assert.NotEqual(senderTriggerId, _receivedTriggerId);
    }

    [Fact]
    public async Task BrokerDroppingEveryConnection_GetsOneConnectAttemptPerInterval()
    {
        var listener = new TcpListener(IPAddress.Loopback, _port);
        listener.Start();
        _ = DropEveryConnectionAsync(listener);
        CreateClient();

        await Task.Delay(ObservationWindow);
        listener.Stop();

        Assert.InRange(Volatile.Read(ref _connectAttempts), 1, MaxAttemptsInWindow);
        Assert.InRange(Volatile.Read(ref _failuresLogged), 1, MaxAttemptsInWindow);
    }

    [Fact]
    public async Task SendsWhileTheBrokerIsDown_DoNotAddConnectAttempts()
    {
        var listener = new TcpListener(IPAddress.Loopback, _port);
        listener.Start();
        _ = DropEveryConnectionAsync(listener);
        var client = CreateClient();

        for (var i = 0; i < SendsDuringOutage; i++)
        {
            await Task.Delay(SendSpacing);
            client.Send(Topic, "command");
        }
        await Task.Delay(ObservationWindow - SendSpacing * SendsDuringOutage);
        listener.Stop();

        Assert.InRange(Volatile.Read(ref _connectAttempts), 1, MaxAttemptsInWindow);
    }

    [Fact]
    public async Task BrokerRejectingTheCredentials_RaisesAConnectionIssue()
    {
        var listener = new TcpListener(IPAddress.Loopback, _port);
        listener.Start();
        _ = RejectEveryConnectAsync(listener);
        var client = CreateClient("user", "wrong");

        await TestNetwork.WaitUntilAsync(
            () => client.GetIssues().Any(issue => issue.Key == CommunicationClient.ConnectionIssueKey), 10,
            "The rejected connection was never reported");
        listener.Stop();

        Assert.Contains(client.GetIssues(), issue => issue.Message.Contains("BadUserNameOrPassword"));
    }
}
