using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Moq;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace AVCoders.CommunicationClients.Tests;

public class AvCodersSshClientTest : IDisposable
{
    private const int QueueTimeoutSeconds = 5;
    private const int ShellBufferSize = 1024;
    private const int Senders = 4;
    private const int MessagesPerSender = 25;
    private const int MessageLength = 3 * ShellBufferSize;
    private static readonly TimeSpan AgeMargin = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SessionRebuild = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReconnectingSendQueueTimeout = TimeSpan.FromSeconds(15);
    private const string AbortedByTheServer = "Renci.SshNet.Common.SshConnectionException: An established connection was aborted by the server.";
    private static readonly byte[] ServerIdentification = "SSH-2.0-OpenSSH_9.6\r\n"u8.ToArray();
    private readonly FakeTimeProvider _time = new();
    private readonly TcpListener _droppingServer = new(IPAddress.Loopback, 0);
    private readonly AvCodersSshClient _client;

    public AvCodersSshClientTest()
    {
        _droppingServer.Start();
        _ = DropEveryConnection(_droppingServer);
        _client = CreateClient((ushort)((IPEndPoint)_droppingServer.LocalEndpoint).Port);
    }

    public void Dispose()
    {
        try { _client.Disconnect(); } catch { }
        _droppingServer.Stop();
    }

    private AvCodersSshClient CreateClient(ushort port) =>
        new("127.0.0.1", port, "user", "password", "TestSshClient", CommandStringFormat.Ascii, timeProvider: _time);

    private static async Task DropEveryConnection(TcpListener server)
    {
        try
        {
            while (true)
                (await server.AcceptTcpClientAsync()).Dispose();
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
        }
    }

    internal static Task UntilTheFirstAttemptFailed(AvCodersSshClient client) =>
        Wait.UntilAsync(() => client.ConnectionState == ConnectionState.Disconnected && client.GetIssues().Any(),
            "the failed connection was never reported");

    private Task UntilTheFirstAttemptFailed() => UntilTheFirstAttemptFailed(_client);

    private async Task DisconnectAfterTheFirstFailedAttempt()
    {
        await UntilTheFirstAttemptFailed();
        _client.Disconnect();
    }

    [Fact]
    public async Task Disconnect_StopsAllWorkers_AndReconnectLeavesThemStopped()
    {
        await DisconnectAfterTheFirstFailedAttempt();
        var states = new ConcurrentQueue<ConnectionState>();
        _client.ConnectionStateHandlers += states.Enqueue;

        _client.Reconnect();

        Assert.All(TestNetwork.Workers(_client), w => Assert.False(w.IsRunning));
        Assert.Equal(ConnectionState.Disconnected, _client.DesiredConnectionState);
        Assert.Empty(states);
    }

    [Fact]
    public async Task Send_WhileDisconnectedOnRequest_IsDroppedWithoutRaisingAnError()
    {
        await DisconnectAfterTheFirstFailedAttempt();
        var requests = 0;
        _client.RequestHandlers += _ => requests++;
        _client.RequestByteHandlers += _ => requests++;

        _client.Send("dropped");
        _client.Send("dropped"u8.ToArray());

        Assert.Equal(0, requests);
        Assert.Equal(0, TestNetwork.SendQueueCount(_client));
        Assert.Equal(ConnectionState.Disconnected, _client.ConnectionState);
    }

    [Fact]
    public void Send_WhileOffline_QueuesWithTheSendersTriggerIdAndRaisesNoTx()
    {
        var requests = 0;
        _client.RequestHandlers += _ => Interlocked.Increment(ref requests);
        _client.RequestByteHandlers += _ => Interlocked.Increment(ref requests);

        var triggerId = Trigger.Start();
        _client.Send("queued text");
        _client.Send("queued bytes"u8.ToArray());

        Assert.Equal(0, requests);
        Assert.Equal(new List<string?> { triggerId, triggerId }, QueuedTriggerIds());
    }

    [Fact]
    public async Task Send_WhileOffline_QueuesWithoutChangingTheConnectionState()
    {
        await UntilTheFirstAttemptFailed();
        var states = new ConcurrentQueue<ConnectionState>();
        _client.ConnectionStateHandlers += states.Enqueue;

        _client.Send("queued text");
        _client.Send("queued bytes"u8.ToArray());

        Assert.Equal(2, TestNetwork.SendQueueCount(_client));
        Assert.Empty(states);
        Assert.Equal(ConnectionState.Disconnected, _client.ConnectionState);
    }

    [Fact]
    public async Task Send_WhileTheSshClientIsDisposedDuringAReconnect_QueuesTheMessage()
    {
        await UntilTheFirstAttemptFailed();
        var disposed = new Renci.SshNet.SshClient("127.0.0.1", "user", "password");
        disposed.Dispose();
        typeof(AvCodersSshClient)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_client, disposed);

        _client.Send("queued while the client is replaced");

        Assert.Throws<ObjectDisposedException>(() => disposed.IsConnected);
        Assert.Equal(1, TestNetwork.SendQueueCount(_client));
    }

    [Fact]
    public void Connect_OffersPasswordAuthenticationBeforeKeyboardInteractive()
    {
        var connectionInfo = (Renci.SshNet.ConnectionInfo)typeof(AvCodersSshClient)
            .GetMethod("CreateConnectionInfo", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_client, null)!;

        Assert.Collection(connectionInfo.AuthenticationMethods,
            method => Assert.IsType<Renci.SshNet.PasswordAuthenticationMethod>(method),
            method => Assert.IsType<Renci.SshNet.KeyboardInteractiveAuthenticationMethod>(method));
    }

    [Fact]
    public void RejectedCredentials_AreCredentialsRejected_DescribedWithTheUserAndTheWaitBeforeTheNextAttempt()
    {
        var rejected = new SshAuthenticationException("Permission denied (password).");

        Assert.Equal((ConnectionReasonCategory.CredentialsRejected,
                "SSH authentication to 127.0.0.1 was rejected for user user; the next attempt is in 5 minutes"),
            ExplainConnectionError(rejected));
    }

    [Fact]
    public void Constructor_SetsTheDesiredConnectionStateToConnected()
    {
        Assert.Equal(ConnectionState.Connected, _client.DesiredConnectionState);
    }

    [Fact]
    public void AnOperationTimeout_IsTimedOut()
    {
        var timedOut = new SshOperationTimeoutException("Socket read operation has timed out after 30000 milliseconds.");

        Assert.Equal((ConnectionReasonCategory.TimedOut, $"The SSH connection to 127.0.0.1:{_client.Port} timed out"),
            ExplainConnectionError(timedOut));
    }

    [Fact]
    public async Task ADeviceThatHangsUpAfterItsBanner_IsClosedByDevice()
    {
        var (_, reason, port) = await HangUpAfterTheBanner();

        Assert.Equal(ConnectionReasonCategory.ClosedByDevice, reason.Category);
        Assert.Equal($"The SSH connection to 127.0.0.1:{port} failed: An established connection was aborted by the server.", reason.Description);
        Assert.Equal(AbortedByTheServer, reason.RawException);
    }

    [Fact]
    public async Task ASessionTheDeviceAborts_IsClosedByDevice_WithTheRawException()
    {
        var (aborted, _, _) = await HangUpAfterTheBanner();
        await StopWorkers();
        UseSession(ShellStreamWithoutAServer());
        Assert.Null(_client.ConnectionReason);

        typeof(AvCodersSshClient).GetMethod("ClientOnErrorOccurred", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_client, [null, new ExceptionEventArgs(aborted)]);

        Assert.Equal(ConnectionState.Error, _client.ConnectionState);
        var reason = _client.ConnectionReason!;
        Assert.Equal(ConnectionReasonCategory.ClosedByDevice, reason.Category);
        Assert.Equal(AbortedByTheServer, reason.RawException);
    }

    [Fact]
    public async Task Disconnect_IsRequested()
    {
        await DisconnectAfterTheFirstFailedAttempt();

        Assert.Equal(ConnectionReasonCategory.Requested, _client.ConnectionReason!.Category);
    }

    private async Task DisconnectByAPersonAfterTheFirstFailedAttempt()
    {
        await UntilTheFirstAttemptFailed();
        AsAPerson.Run(_client.Disconnect);
    }

    [Fact]
    public async Task Disconnect_ByAPerson_TurnsReconnectOnSendOn()
    {
        await DisconnectByAPersonAfterTheFirstFailedAttempt();

        Assert.True(_client.ReconnectOnSend);
        Assert.Equal(AsAPerson.Browser, _client.DisconnectedBy);
    }

    [Fact]
    public async Task Disconnect_ByAPerson_RecordsWhoBeforeTheStateAndReasonHandlersRun()
    {
        await UntilTheFirstAttemptFailed();
        var seen = new ConcurrentQueue<(Actor? DisconnectedBy, bool ReconnectOnSend)>();
        _client.ConnectionStateHandlers += _ => seen.Enqueue((_client.DisconnectedBy, _client.ReconnectOnSend));
        _client.ConnectionReasonHandlers += _ => seen.Enqueue((_client.DisconnectedBy, _client.ReconnectOnSend));

        AsAPerson.Run(_client.Disconnect);

        Assert.NotEmpty(seen);
        Assert.All(seen, handled => Assert.Equal((AsAPerson.Browser, true), handled));
    }

    [Fact]
    public async Task Send_ByAPersonAfterAPersonDisconnected_ReconnectsAndWritesIt()
    {
        await DisconnectByAPersonAfterTheFirstFailedAttempt();
        var states = new ConcurrentQueue<ConnectionState>();
        _client.ConnectionStateHandlers += states.Enqueue;
        var sent = new ConcurrentQueue<string>();
        _client.RequestHandlers += sent.Enqueue;

        AsAPerson.Run(() => _client.Send("power on"));
        await Wait.UntilAsync(() => states.Contains(ConnectionState.Connecting), "the send never started a connection attempt");
        await StopWorkers();
        UseSession(ShellStreamWithoutAServer());
        await ProcessSendQueue();

        Assert.Equal(ConnectionState.Connected, _client.DesiredConnectionState);
        Assert.Equal(["power on"], sent);
        Assert.False(_client.ReconnectingForSend);
    }

    [Fact]
    public async Task Send_WithNoPersonAfterAPersonDisconnected_StaysDisconnected()
    {
        await DisconnectByAPersonAfterTheFirstFailedAttempt();
        var states = new ConcurrentQueue<ConnectionState>();
        _client.ConnectionStateHandlers += states.Enqueue;

        _client.Send("poll");

        Assert.Equal(ConnectionState.Disconnected, _client.DesiredConnectionState);
        Assert.Equal(0, TestNetwork.SendQueueCount(_client));
        Assert.Empty(states);
        Assert.True(_client.ReconnectOnSend);
    }

    [Fact]
    public async Task Send_ByAPersonWithReconnectOnSendOff_StaysDisconnected()
    {
        await DisconnectByAPersonAfterTheFirstFailedAttempt();
        _client.SetReconnectOnSend(false);
        var states = new ConcurrentQueue<ConnectionState>();
        _client.ConnectionStateHandlers += states.Enqueue;

        AsAPerson.Run(() => _client.Send("power on"));

        Assert.Equal(ConnectionState.Disconnected, _client.DesiredConnectionState);
        Assert.Equal(0, TestNetwork.SendQueueCount(_client));
        Assert.Empty(states);
    }

    private async Task<List<string>> WrittenAfterAPressOfTwoCommandsAndAPollWaited(TimeSpan wait)
    {
        await DisconnectByAPersonAfterTheFirstFailedAttempt();
        var states = new ConcurrentQueue<ConnectionState>();
        _client.ConnectionStateHandlers += states.Enqueue;
        var sent = new ConcurrentQueue<string>();
        _client.RequestHandlers += sent.Enqueue;
        AsAPerson.Run(() =>
        {
            _client.Send("power on");
            _client.Send("input 2");
        });
        _client.Send("poll");
        await Wait.UntilAsync(() => states.Contains(ConnectionState.Connecting), "the send never started a connection attempt");
        await StopWorkers();

        await TestNetwork.Advance(_time, wait);
        UseSession(ShellStreamWithoutAServer());
        await ProcessSendQueue();
        return sent.ToList();
    }

    [Fact]
    public async Task Send_ByAPersonDuringTheReconnect_WaitsUpToFifteenSeconds_WhileAPollKeepsTheQueueTimeout()
    {
        var written = await WrittenAfterAPressOfTwoCommandsAndAPollWaited(ReconnectingSendQueueTimeout - AgeMargin);

        Assert.Equal(["power on", "input 2"], written);
    }

    [Fact]
    public async Task Send_ByAPersonDuringTheReconnect_IsDroppedAfterFifteenSeconds()
    {
        var written = await WrittenAfterAPressOfTwoCommandsAndAPollWaited(ReconnectingSendQueueTimeout + AgeMargin);

        Assert.Empty(written);
    }

    [Fact]
    public async Task Send_ByAPersonDuringTheReconnect_KeepsALongerQueueTimeout()
    {
        _client.SetQueueTimeout(60);

        var written = await WrittenAfterAPressOfTwoCommandsAndAPollWaited(ReconnectingSendQueueTimeout + AgeMargin);

        Assert.Equal(["power on", "input 2", "poll"], written);
    }

    private async Task<(SshConnectionException Exception, ConnectionReason Reason, ushort Port)> HangUpAfterTheBanner()
    {
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        _ = SendTheBannerThenHangUp(server);
        var client = CreateClient((ushort)((IPEndPoint)server.LocalEndpoint).Port);
        try
        {
            await UntilTheFirstAttemptFailed(client);
            return (client.Errors.Select(error => error.Exception).OfType<SshConnectionException>().Last(),
                client.ConnectionReason!, client.Port);
        }
        finally
        {
            client.Disconnect();
            server.Stop();
        }
    }

    private static async Task SendTheBannerThenHangUp(TcpListener server)
    {
        try
        {
            while (true)
            {
                using var connection = await server.AcceptTcpClientAsync();
                var stream = connection.GetStream();
                await stream.WriteAsync(ServerIdentification);
                connection.Client.Shutdown(SocketShutdown.Send);
                var buffer = new byte[ShellBufferSize];
                while (await stream.ReadAsync(buffer) > 0)
                {
                }
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or IOException)
        {
        }
    }

    [Fact]
    public async Task Reconnect_DuringTheWaitAfterAFailedAttempt_TriesAgainAtOnce()
    {
        await UntilTheFirstAttemptFailed();
        var states = new ConcurrentQueue<ConnectionState>();
        _client.ConnectionStateHandlers += states.Enqueue;

        _client.Reconnect();

        await Wait.UntilAsync(() => states.Contains(ConnectionState.Connecting),
            "the client waited out the failure wait instead of trying again at once");
    }

    private (ConnectionReasonCategory Category, string Description) ExplainConnectionError(Exception e) =>
        ((ConnectionReasonCategory, string))typeof(AvCodersSshClient)
            .GetMethod("ExplainConnectionError", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_client, [e])!;

    private List<string?> QueuedTriggerIds()
    {
        var queue = (IEnumerable)typeof(AvCodersSshClient)
            .GetField("_sendQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_client)!;
        return queue.Cast<object>()
            .Select(item => (string?)item.GetType().GetProperty("TriggerId")!.GetValue(item))
            .ToList();
    }

    [Fact]
    public async Task Connect_AfterDisconnect_TriesToConnectAgain()
    {
        await DisconnectAfterTheFirstFailedAttempt();
        var states = new ConcurrentQueue<ConnectionState>();
        _client.ConnectionStateHandlers += states.Enqueue;

        _client.Connect();

        await Wait.UntilAsync(() => states.Contains(ConnectionState.Connecting), "the client never tried to connect after Connect");
        Assert.Equal(ConnectionState.Connected, _client.DesiredConnectionState);
    }

    [Fact]
    public async Task AQueuedMessageWhoseWriteFails_GetsTheWholeQueueTimeoutAfterTheSessionRebuild()
    {
        foreach (var worker in TestNetwork.Workers(_client))
            await worker.Stop();
        _client.SetQueueTimeout(QueueTimeoutSeconds);
        var sent = new List<string>();
        _client.RequestHandlers += sent.Add;
        _client.Send("first");
        _client.Send("requeued");
        _client.Send("stale");
        await TestNetwork.Advance(_time, TimeSpan.FromSeconds(QueueTimeoutSeconds) - AgeMargin);
        var droppedSession = ShellStreamWithoutAServer();
        _client.RequestHandlers += _ => droppedSession.Dispose();
        UseSession(droppedSession);

        await ProcessSendQueue();
        var stateAfterTheFailure = _client.ConnectionState;
        await TestNetwork.Advance(_time, SessionRebuild);
        UseSession(ShellStreamWithoutAServer());
        await ProcessSendQueue();

        Assert.Equal(ConnectionState.Error, stateAfterTheFailure);
        Assert.Equal(["first", "requeued"], sent);
    }

    [Fact]
    public async Task ConcurrentSenders_EachMessageReachesTheStreamWholeAndInOrder()
    {
        await StopWorkers();
        var (stream, written) = RecordingShellStream();
        UseSession(stream);

        await Task.WhenAll(Enumerable.Range(0, Senders).Select(sender => Task.Run(() =>
        {
            for (var index = 0; index < MessagesPerSender; index++)
                _client.Send(Message(sender, index));
        })));

        var messages = written.SelectMany(chunk => chunk).Chunk(MessageLength).ToList();
        Assert.Equal(Senders * MessagesPerSender, messages.Count);
        Assert.All(messages, message => Assert.Equal(Message(message[0], message[1]), message));
        Assert.All(Enumerable.Range(0, Senders), sender =>
            Assert.Equal(Enumerable.Range(0, MessagesPerSender), messages.Where(message => message[0] == sender).Select(message => (int)message[1])));
    }

    [Fact]
    public async Task LinesFromTheDevice_AreReadAsTheyArrive_InOrder()
    {
        await StopWorkers();
        var stream = ShellStreamWithoutAServer();
        UseSession(stream);
        var lines = new ConcurrentQueue<string>();
        _client.ResponseHandlers += lines.Enqueue;
        await TestNetwork.Workers(_client)[0].Restart();

        DeviceSends(stream, "first line\r\nsecond line\r\n");
        await Wait.UntilAsync(() => lines.Count == 2, "the first two lines were never read");
        DeviceSends(stream, "third line\r\n");
        await Wait.UntilAsync(() => lines.Count == 3, "the third line was never read");

        Assert.Equal(["first line", "second line", "third line"], lines);
    }

    [Fact]
    public async Task AShellTheDeviceCloses_IsNoticedWhenItCloses()
    {
        await StopWorkers();
        var stream = ShellStreamWithoutAServer();
        UseSession(stream);
        await TestNetwork.Workers(_client)[0].Restart();

        typeof(ShellStream).GetMethod("Channel_Closed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(stream, [null, Activator.CreateInstance(SshNetType("Renci.SshNet.Common.ChannelEventArgs"), 0u)]);

        await Wait.UntilAsync(() => _client.ConnectionState == ConnectionState.Error, "the closed shell was never noticed");
    }

    private async Task StopWorkers()
    {
        foreach (var worker in TestNetwork.Workers(_client))
            await worker.Stop();
    }

    private static byte[] Message(int sender, int index) =>
        [(byte)sender, (byte)index, .. Enumerable.Repeat((byte)('A' + sender), MessageLength - 2)];

    private static void DeviceSends(ShellStream stream, string text) =>
        typeof(ShellStream).GetMethod("Channel_DataReceived", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(stream, [null, Activator.CreateInstance(SshNetType("Renci.SshNet.Common.ChannelDataEventArgs"),
                0u, new ArraySegment<byte>(Encoding.ASCII.GetBytes(text)))]);

    private static Type SshNetType(string name) => typeof(ShellStream).Assembly.GetType(name)!;

    private static (ShellStream Stream, ConcurrentQueue<byte[]> Written) RecordingShellStream()
    {
        var stream = ShellStreamWithoutAServer();
        var written = new ConcurrentQueue<byte[]>();
        var channel = typeof(ShellStream).GetField("_channel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(stream)!;
        var channelType = SshNetType("Renci.SshNet.Channels.IChannelSession");
        var channelMock = (Mock)typeof(Mock).GetMethod(nameof(Mock.Get))!.MakeGenericMethod(channelType).Invoke(null, [channel])!;
        var sendData = channelType.GetInterfaces().SelectMany(type => type.GetMethods())
            .Single(method => method.Name == "SendData" && method.GetParameters().Length == 3);
        var target = Expression.Parameter(channelType);
        var anyValue = typeof(It).GetMethod(nameof(It.IsAny))!;
        var sendAnything = Expression.Lambda(typeof(Action<>).MakeGenericType(channelType),
            Expression.Call(target, sendData,
                Expression.Call(anyValue.MakeGenericMethod(typeof(byte[]))),
                Expression.Call(anyValue.MakeGenericMethod(typeof(int))),
                Expression.Call(anyValue.MakeGenericMethod(typeof(int)))),
            target);
        var setup = channelMock.GetType().GetMethods()
            .Single(method => method.Name == nameof(Mock<object>.Setup) && !method.IsGenericMethod
                              && method.GetParameters()[0].ParameterType.IsInstanceOfType(sendAnything))
            .Invoke(channelMock, [sendAnything])!;
        setup.GetType().GetInterfaces().SelectMany(type => type.GetMethods())
            .First(method => method.Name == "Callback" && method.IsGenericMethod && method.GetGenericArguments().Length == 3)
            .MakeGenericMethod(typeof(byte[]), typeof(int), typeof(int))
            .Invoke(setup, [new Action<byte[], int, int>((data, offset, count) => written.Enqueue(data[offset..(offset + count)]))]);
        return (stream, written);
    }

    private static Renci.SshNet.ShellStream ShellStreamWithoutAServer()
    {
        var sessionType = typeof(Renci.SshNet.SshClient).Assembly.GetType("Renci.SshNet.ISession")!;
        var session = (Mock)Activator.CreateInstance(typeof(Mock<>).MakeGenericType(sessionType))!;
        session.DefaultValue = DefaultValue.Mock;
        var stream = (Renci.SshNet.ShellStream)typeof(Renci.SshNet.ShellStream)
            .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, [sessionType, typeof(int), typeof(bool)])!
            .Invoke([session.Object, ShellBufferSize, true]);
        SetField(stream, "_encoding", Encoding.UTF8);
        SetField(stream, "_carriageReturnBytes", "\r"u8.ToArray());
        SetField(stream, "_lineFeedBytes", "\n"u8.ToArray());
        return stream;
    }

    private static void SetField(ShellStream stream, string name, object value) =>
        typeof(ShellStream).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(stream, value);

    private void UseSession(Renci.SshNet.ShellStream stream)
    {
        var connected = new Mock<Renci.SshNet.SshClient>("127.0.0.1", "user", "password");
        connected.Setup(client => client.IsConnected).Returns(true);
        typeof(AvCodersSshClient).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_client, connected.Object);
        typeof(AvCodersSshClient).GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_client, stream);
        typeof(AvCodersSshClient).GetMethod("Subscribe", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_client, [stream]);
        typeof(CommunicationClient).GetProperty(nameof(CommunicationClient.ConnectionState))!
            .SetValue(_client, ConnectionState.Connected);
    }

    private Task ProcessSendQueue()
    {
        var pass = (Task)typeof(AvCodersSshClient)
            .GetMethod("ProcessSendQueue", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_client, [CancellationToken.None])!;
        return TestNetwork.UntilAsync(_time, () => pass.IsCompleted, "the send queue pass never finished");
    }
}

public class AvCodersSshClientFailureWaitTest
{
    private static readonly TimeSpan FailureWait = TimeSpan.FromSeconds(60);
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public async Task ANetworkFailure_WaitsSixtySecondsBeforeTheNextAttempt()
    {
        var refused = new AvCodersSshClient("127.0.0.1", TestNetwork.GetFreePort(), "user", "password", "TestSshClient", CommandStringFormat.Ascii, timeProvider: _time);
        try
        {
            await AvCodersSshClientTest.UntilTheFirstAttemptFailed(refused);
            var states = new ConcurrentQueue<ConnectionState>();
            refused.ConnectionStateHandlers += states.Enqueue;

            await Task.Run(() => _time.Advance(FailureWait - TestNetwork.ClockStep));

            Assert.Equal($"127.0.0.1:{refused.Port} refused the connection", refused.GetIssues().Single().Message);
            Assert.Equal(ConnectionReasonCategory.Refused, refused.ConnectionReason!.Category);
            Assert.Equal($"127.0.0.1:{refused.Port} refused the connection", refused.ConnectionReason.Description);
            Assert.DoesNotContain(ConnectionState.Connecting, states);
            await TestNetwork.UntilAsync(_time, () => states.Contains(ConnectionState.Connecting),
                "the client never tried again after the failure wait");
        }
        finally
        {
            refused.Disconnect();
        }
    }
}
