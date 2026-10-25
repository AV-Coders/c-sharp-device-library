using System.Collections;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Reflection;
using Moq;
using Renci.SshNet.Common;

namespace AVCoders.CommunicationClients.Tests;

public class AvCodersSshClientTest : IDisposable
{
    private const int QueueTimeoutSeconds = 5;
    private const int ShellBufferSize = 1024;
    private static readonly TimeSpan AgeMargin = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SessionRebuild = TimeSpan.FromSeconds(2);
    private readonly AvCodersSshClient _client =
        new("127.0.0.1", TestNetwork.GetFreePort(), "user", "password", "TestSshClient");

    public void Dispose()
    {
        try { _client.Disconnect(); } catch { /* test teardown */ }
    }

    private async Task DisconnectAfterTheFirstFailedAttempt()
    {
        await TestNetwork.WaitUntilAsync(
            () => _client.ConnectionState == ConnectionState.Disconnected && _client.GetIssues().Any(),
            30, "the refused connection was never reported");
        _client.Disconnect();
    }

    [Fact]
    public async Task Disconnect_StopsAllWorkers()
    {
        await DisconnectAfterTheFirstFailedAttempt();

        Assert.All(TestNetwork.Workers(_client), w => Assert.False(w.IsRunning));
        Assert.Equal(ConnectionState.Disconnected, _client.DesiredConnectionState);
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
        await TestNetwork.WaitUntilAsync(
            () => _client.ConnectionState == ConnectionState.Disconnected && _client.GetIssues().Any(),
            30, "the refused connection was never reported");
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
        await TestNetwork.WaitUntilAsync(
            () => _client.ConnectionState == ConnectionState.Disconnected && _client.GetIssues().Any(),
            30, "the refused connection was never reported");
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
        var sshClient = (Renci.SshNet.SshClient)typeof(AvCodersSshClient)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_client)!;

        Assert.Collection(sshClient.ConnectionInfo.AuthenticationMethods,
            method => Assert.IsType<Renci.SshNet.PasswordAuthenticationMethod>(method),
            method => Assert.IsType<Renci.SshNet.KeyboardInteractiveAuthenticationMethod>(method));
    }

    [Fact]
    public void RejectedCredentials_WaitFiveMinutesBeforeTheNextAttempt()
    {
        var rejected = new SshAuthenticationException("Permission denied (password).");

        Assert.Equal(TimeSpan.FromMinutes(5), RetryWaitAfter(rejected));
        Assert.Equal("SSH authentication to 127.0.0.1 was rejected for user user; the next attempt is in 5 minutes",
            DescribeSshConnectionError(rejected));
    }

    [Fact]
    public async Task ANetworkFailure_KeepsTheSixtySecondWait()
    {
        await TestNetwork.WaitUntilAsync(
            () => _client.ConnectionState == ConnectionState.Disconnected && _client.GetIssues().Any(),
            30, "the refused connection was never reported");

        Assert.Equal($"127.0.0.1:{_client.Port} refused the connection", _client.GetIssues().Single().Message);
        Assert.All(
            new Exception[]
            {
                new SocketException((int)SocketError.ConnectionRefused),
                new SshConnectionException("An established connection was aborted by the server."),
                new SshOperationTimeoutException("Socket read operation has timed out after 30000 milliseconds.")
            },
            e => Assert.Equal(TimeSpan.FromSeconds(60), RetryWaitAfter(e)));
    }

    [Fact]
    public async Task Reconnect_DuringTheWaitAfterAFailedAttempt_TriesAgainAtOnce()
    {
        await TestNetwork.WaitUntilAsync(
            () => _client.ConnectionState == ConnectionState.Disconnected && _client.GetIssues().Any(),
            30, "the refused connection was never reported");
        var states = new ConcurrentQueue<ConnectionState>();
        _client.ConnectionStateHandlers += states.Enqueue;

        _client.Reconnect();

        await TestNetwork.WaitUntilAsync(() => states.Contains(ConnectionState.Connecting),
            15, "the client waited out the failure wait instead of trying again at once");
    }

    private static TimeSpan RetryWaitAfter(Exception e) =>
        (TimeSpan)typeof(AvCodersSshClient)
            .GetMethod("RetryWaitAfter", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { e })!;

    private string DescribeSshConnectionError(Exception e) =>
        (string)typeof(AvCodersSshClient)
            .GetMethod("DescribeSshConnectionError", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_client, new object[] { e })!;

    private List<string?> QueuedTriggerIds()
    {
        var queue = (IEnumerable)typeof(AvCodersSshClient)
            .GetField("_sendQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_client)!;
        return queue.Cast<object>()
            .Select(item => (string?)item.GetType().GetProperty("TriggerId")!.GetValue(item))
            .ToList();
    }

    [Fact]
    public async Task Reconnect_WhileDisconnectedOnRequest_DoesNothing()
    {
        await DisconnectAfterTheFirstFailedAttempt();
        var states = new ConcurrentQueue<ConnectionState>();
        _client.ConnectionStateHandlers += states.Enqueue;

        _client.Reconnect();
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.Empty(states);
    }

    [Fact]
    public async Task Connect_AfterDisconnect_TriesToConnectAgain()
    {
        await DisconnectAfterTheFirstFailedAttempt();
        var states = new ConcurrentQueue<ConnectionState>();
        _client.ConnectionStateHandlers += states.Enqueue;

        _client.Connect();

        await TestNetwork.WaitUntilAsync(() => states.Contains(ConnectionState.Connecting),
            15, "the client never tried to connect after Connect");
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
        TestNetwork.SetQueuedAge<(string, byte[]?)>(_client, TimeSpan.FromSeconds(QueueTimeoutSeconds) - AgeMargin);
        var droppedSession = ShellStreamWithoutAServer();
        _client.RequestHandlers += _ => droppedSession.Dispose();
        UseSession(droppedSession);

        await ProcessSendQueue();
        var stateAfterTheFailure = _client.ConnectionState;
        TestNetwork.AgeTheQueue<(string, byte[]?)>(_client, SessionRebuild);
        UseSession(ShellStreamWithoutAServer());
        await ProcessSendQueue();

        Assert.Equal(ConnectionState.Error, stateAfterTheFailure);
        Assert.Equal(["first", "requeued"], sent);
    }

    private static Renci.SshNet.ShellStream ShellStreamWithoutAServer()
    {
        var sessionType = typeof(Renci.SshNet.SshClient).Assembly.GetType("Renci.SshNet.ISession")!;
        var session = (Mock)Activator.CreateInstance(typeof(Mock<>).MakeGenericType(sessionType))!;
        session.DefaultValue = DefaultValue.Mock;
        return (Renci.SshNet.ShellStream)typeof(Renci.SshNet.ShellStream)
            .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, [sessionType, typeof(int), typeof(bool)])!
            .Invoke([session.Object, ShellBufferSize, true]);
    }

    private void UseSession(Renci.SshNet.ShellStream stream)
    {
        var connected = new Mock<Renci.SshNet.SshClient>("127.0.0.1", "user", "password");
        connected.Setup(client => client.IsConnected).Returns(true);
        typeof(AvCodersSshClient).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_client, connected.Object);
        typeof(AvCodersSshClient).GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_client, stream);
        typeof(CommunicationClient).GetProperty(nameof(CommunicationClient.ConnectionState))!
            .SetValue(_client, ConnectionState.Connected);
    }

    private Task ProcessSendQueue() =>
        (Task)typeof(AvCodersSshClient)
            .GetMethod("ProcessSendQueue", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_client, [CancellationToken.None])!;
}
