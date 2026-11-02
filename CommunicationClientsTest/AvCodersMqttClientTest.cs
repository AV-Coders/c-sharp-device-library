using System.Collections.Concurrent;
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
    private const string OtherTopic = "lobby/status";
    private const int AttemptsToObserve = 3;
    private static readonly byte[] ConnectAck = [0x20, 0x03, 0x00, 0x00, NoProperties];
    private static readonly byte[] BadCredentialsConnectAck = [0x20, 0x03, 0x00, 0x86, NoProperties];
    private static readonly byte[] PingResponse = [0xD0, 0x00];
    private static readonly byte[] ReservedPacketType = [0x00, 0x00];
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ClosedWithin = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReportedWithin = TimeSpan.FromSeconds(10);
    private static readonly Actor Sender = new(ActorKind.Browser, "10.0.0.1", "Test agent", null, "connection-1");

    private readonly ConnectTimeoutClock _time = new();
    private readonly ushort _port = TestNetwork.GetFreePort();
    private readonly List<AvCodersMqttClient> _clients = new();
    private readonly object _receivedLock = new();
    private bool _received;
    private Actor? _receivedActor;
    private string? _receivedTriggerId;
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

    private static async Task ServeSessionAsync(System.Net.Sockets.TcpClient connection, ConcurrentQueue<byte>? packetTypes = null)
    {
        var stream = connection.GetStream();
        while (true)
        {
            var (type, body) = await ReadPacketAsync(stream);
            packetTypes?.Enqueue(type);
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

    private static async Task DropEveryConnectionAsync(TcpListener listener)
    {
        try
        {
            while (true)
            {
                using var connection = await listener.AcceptTcpClientAsync();
            }
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task AnswerEveryConnectWithAReservedPacketTypeAsync(TcpListener listener)
    {
        try
        {
            while (true)
            {
                using var connection = await listener.AcceptTcpClientAsync();
                await ReadPacketAsync(connection.GetStream());
                await connection.GetStream().WriteAsync(ReservedPacketType);
                await ReadPacketAsync(connection.GetStream());
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or IOException or EndOfStreamException)
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
        var client = new AvCodersMqttClient("127.0.0.1", _port, username, password, "TestMqttClient", _time);
        client.ErrorsUpdated += () => Interlocked.Increment(ref _failuresLogged);
        _clients.Add(client);
        return client;
    }

    private Task UntilTheFirstAttemptFailed(AvCodersMqttClient client) =>
        Wait.UntilAsync(() => client.ConnectionState == ConnectionState.Disconnected,
            "the first connection attempt never failed");

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
        _time.ConnectTimeouts = ConnectTimeouts.AtOnce;
        var client = CreateClient();
        client.SubscribeToTopic(Topic, RecordReceived);
        client.Send(Topic, "command");
        await UntilTheFirstAttemptFailed(client);

        var listener = TestNetwork.ListenOn(_time, _port);
        _time.ConnectTimeouts = ConnectTimeouts.Never;
        using var connection = await TestNetwork.AcceptAsync(listener, _time);
        _ = ServeSessionAsync(connection);
        await TestNetwork.UntilAsync(_time, () => { lock (_receivedLock) return _received; },
            "The subscribed message never arrived");
        listener.Stop();

        Assert.Null(_receivedActor);
        Assert.NotNull(_receivedTriggerId);
        Assert.NotEqual(senderTriggerId, _receivedTriggerId);
    }

    [Fact]
    public async Task Connecting_SubscribesEveryTopicBeforeAConnectedHandlerPublishes()
    {
        var packetTypes = new ConcurrentQueue<byte>();
        _time.ConnectTimeouts = ConnectTimeouts.AtOnce;
        var client = CreateClient();
        client.SubscribeToTopic(Topic, _ => { });
        client.SubscribeToTopic(OtherTopic, _ => { });
        client.ConnectionStateHandlers += state =>
        {
            if (state == ConnectionState.Connected)
                client.Send($"{Topic}/get", "{}");
        };
        await UntilTheFirstAttemptFailed(client);

        var listener = TestNetwork.ListenOn(_time, _port);
        _time.ConnectTimeouts = ConnectTimeouts.Never;
        using var connection = await TestNetwork.AcceptAsync(listener, _time);
        _ = ServeSessionAsync(connection, packetTypes);
        await TestNetwork.UntilAsync(_time, () => packetTypes.Contains(PublishPacket),
            "The Connected handler's publish never reached the broker");
        listener.Stop();

        Assert.Equal([ConnectPacket, SubscribePacket, SubscribePacket, PublishPacket], packetTypes.Take(4));
    }

    [Fact]
    public async Task BrokerDroppingEveryConnection_GetsOneConnectAttemptPerInterval_WhileSendsKeepComing()
    {
        var listener = new TcpListener(IPAddress.Loopback, _port);
        listener.Start();
        _ = DropEveryConnectionAsync(listener);
        var attempts = new ConcurrentQueue<DateTimeOffset>();
        var client = CreateClient();
        client.ConnectionStateHandlers += state =>
        {
            if (state == ConnectionState.Connecting)
                attempts.Enqueue(_time.GetUtcNow());
        };

        await TestNetwork.UntilAsync(_time, () =>
        {
            client.Send(Topic, "command");
            return attempts.Count >= AttemptsToObserve;
        }, "the client stopped retrying the broker");
        listener.Stop();

        var attemptTimes = attempts.ToList();
        Assert.All(attemptTimes.Zip(attemptTimes.Skip(1)), pair => Assert.True(pair.Second - pair.First >= ReconnectInterval,
            $"a connect attempt came {pair.Second - pair.First} after the previous one"));
        Assert.InRange(Volatile.Read(ref _failuresLogged), 1, attemptTimes.Count + 1);
    }

    [Fact]
    public async Task BrokerRejectingTheCredentials_RaisesAConnectionIssue()
    {
        var listener = new TcpListener(IPAddress.Loopback, _port);
        listener.Start();
        _ = RejectEveryConnectAsync(listener);
        var client = CreateClient("user", "wrong");

        await Wait.UntilAsync(
            () => client.GetIssues().Any(issue => issue.Key == CommunicationClient.ConnectionIssueKey),
            "The rejected connection was never reported");
        listener.Stop();

        Assert.Contains(client.GetIssues(), issue => issue.Message.Contains("BadUserNameOrPassword"));
    }

    [Fact]
    public async Task BrokerRejectingTheCredentials_IsCredentialsRejected()
    {
        var listener = new TcpListener(IPAddress.Loopback, _port);
        listener.Start();
        _ = RejectEveryConnectAsync(listener);
        var client = CreateClient("user", "wrong");

        await Wait.UntilAsync(() => client.ConnectionReason != null, "The rejected connection never set a reason");
        listener.Stop();

        Assert.Equal(ConnectionReasonCategory.CredentialsRejected, client.ConnectionReason!.Category);
        Assert.Contains("BadUserNameOrPassword", client.ConnectionReason.Description);
    }

    [Fact]
    public async Task BrokerThatAnswersTheConnectWithGarbage_SetsTheReasonFromTheException()
    {
        var listener = new TcpListener(IPAddress.Loopback, _port);
        listener.Start();
        _ = AnswerEveryConnectWithAReservedPacketTypeAsync(listener);
        var client = CreateClient();

        await Wait.UntilAsync(() => client.ConnectionReason != null, "The failed connection never set a reason");
        listener.Stop();

        var reason = client.ConnectionReason!;
        Assert.Equal(ConnectionReasonCategory.Other, reason.Category);
        Assert.StartsWith($"The connection to 127.0.0.1:{_port} failed: ", reason.Description);
        Assert.StartsWith("MQTTnet.Adapter.MqttConnectingFailedException: ", reason.RawException);
        Assert.EndsWith("MQTTnet.Exceptions.MqttProtocolViolationException: The packet type is invalid (0).", reason.RawException);
    }

    [Fact]
    public async Task WildcardSubscription_ReachesItsHandlerAndNotTheHandlersOfOtherFilters()
    {
        var wildcardMessages = 0;
        var otherMessages = 0;
        _time.ConnectTimeouts = ConnectTimeouts.AtOnce;
        var client = CreateClient();
        client.SubscribeToTopic("room/+", _ => Interlocked.Increment(ref wildcardMessages));
        client.SubscribeToTopic("lobby/#", _ => Interlocked.Increment(ref otherMessages));
        await UntilTheFirstAttemptFailed(client);

        var listener = TestNetwork.ListenOn(_time, _port);
        _time.ConnectTimeouts = ConnectTimeouts.Never;
        using var connection = await TestNetwork.AcceptAsync(listener, _time);
        _ = ServeSessionAsync(connection);
        await TestNetwork.UntilAsync(_time, () => Volatile.Read(ref wildcardMessages) == 2,
            "The messages on room/status never reached the room/+ handler");
        listener.Stop();

        Assert.Equal(0, Volatile.Read(ref otherMessages));
    }

    [Fact]
    public async Task BrokerThatNeverAnswersTheConnect_TimesOutAfterFiveSecondsClosesTheConnectionAndConnectsOnTheNextAttempt()
    {
        var listener = new TcpListener(IPAddress.Loopback, _port);
        listener.Start();
        var attemptStarted = _time.GetUtcNow();
        var client = CreateClient();
        var reportedAt = new TaskCompletionSource<DateTimeOffset>();
        client.IssuesChanged += (_, change) =>
        {
            if (change.ChangedIssue?.Key == CommunicationClient.ConnectionIssueKey)
                reportedAt.TrySetResult(_time.GetUtcNow());
        };
        using var silent = await TestNetwork.AcceptAsync(listener, 20);
        var stream = silent.GetStream();
        var (packetType, _) = await ReadPacketAsync(stream);

        await Task.Run(() => _time.Advance(ConnectTimeout - TestNetwork.ClockStep));
        await Task.Run(() => _time.Advance(TestNetwork.ClockStep));
        var reportedAfter = await reportedAt.Task.WaitAsync(ReportedWithin) - attemptStarted;
        var issues = client.GetIssues();
        var reason = client.ConnectionReason;
        await ReadUntilClosedAsync(stream).WaitAsync(ClosedWithin);
        _time.ConnectTimeouts = ConnectTimeouts.Never;
        using var answered = await TestNetwork.AcceptAsync(listener, _time);
        _ = ServeSessionAsync(answered);
        await TestNetwork.UntilAsync(_time, () => client.ConnectionState == ConnectionState.Connected,
            "The attempt after the timeout never connected");
        listener.Stop();

        Assert.Equal(ConnectPacket, packetType);
        Assert.InRange(reportedAfter, ConnectTimeout, ConnectTimeout + TestNetwork.ClockStep);
        Assert.Contains(issues, issue => issue.Message == $"The connection attempt to 127.0.0.1:{_port} timed out");
        Assert.Equal(ConnectionReasonCategory.TimedOut, reason!.Category);
        await Wait.UntilAsync(() => client.ConnectionReason == null, "connecting never cleared the reason");
    }

    private static async Task ReadUntilClosedAsync(NetworkStream stream)
    {
        var buffer = new byte[256];
        while (await stream.ReadAsync(buffer) > 0)
        {
        }
    }
}
