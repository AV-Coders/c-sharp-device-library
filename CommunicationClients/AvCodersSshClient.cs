using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using SshClient = Renci.SshNet.SshClient;
using SshClientBase = AVCoders.Core.SshClient;

namespace AVCoders.CommunicationClients;

public class AvCodersSshClient : SshClientBase
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReceivePollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan ShortSession = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FailureWait = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RejectedCredentialsWait = TimeSpan.FromMinutes(5);
    private readonly string _username;
    private readonly string _password;
    private readonly LinkedList<QueuedPayload<(string Text, byte[]? Bytes)>> _sendQueue = new();
    private readonly object _sessionLock = new();

    private SshClient _client;
    
    private readonly Dictionary<TerminalModes, uint> _modes;
    private ShellStream? _stream;
    private long? _sessionStartedAt;
    private int _consecutiveShortSessions;

    public AvCodersSshClient(string host, ushort port, string username, string password, string name = "", CommandStringFormat commandStringFormat = CommandStringFormat.Ascii)
        : base(host, port, name, commandStringFormat)
    {
        ConnectionState = ConnectionState.Unknown;
        _username = username;
        _password = password;
        
        _client = new SshClient(CreateConnectionInfo());
        
        _modes = new Dictionary<TerminalModes, uint> { {TerminalModes.ECHO, 0} };
        
        ConnectionStateWorker.Restart();
        ReceiveThreadWorker.Restart();
        SendQueueWorker.Restart();
    }

    private ConnectionInfo CreateConnectionInfo()
    {
        var keyboardInteractive = new KeyboardInteractiveAuthenticationMethod(_username);
        keyboardInteractive.AuthenticationPrompt += AuthenticationMethodOnAuthenticationPrompt;
        return new ConnectionInfo(Host, Port, _username,
            new PasswordAuthenticationMethod(_username, _password), keyboardInteractive);
    }

    protected override async Task Receive(CancellationToken token)
    {
        using (PushProperties("Receive"))
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Local copy: Reconnect/CheckConnectionState swap _stream and
                    // _client without stopping this worker. A disposed client lands
                    // in the ObjectDisposedException handler below.
                    var stream = _stream;
                    if (_client.IsConnected && stream != null && ConnectionState != ConnectionState.Error)
                    {
                        if (!stream.CanWrite && ConnectionState == ConnectionState.Connected)
                        {
                            LogWarning("The shell stream is no longer readable, reconnecting");
                            ConnectionState = ConnectionState.Error;
                        }
                        else if (stream.DataAvailable)
                        {
                            var line = stream.ReadLine(ReceiveTimeout);
                            if (!string.IsNullOrEmpty(line))
                            {
                                using (Trigger.Begin())
                                    InvokeResponseHandlers(line, Encoding.UTF8.GetBytes(line));
                            }
                        }
                        else
                        {
                            await Task.Delay(ReceivePollInterval, token);
                        }
                    }
                    else
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), token);
                    }
                }
                catch (Exception e) when (e is ObjectDisposedException)
                {
                    LogWarning("Stream or client was disposed, waiting for reconnection");
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }
            }
        }
    }

    protected override async Task CheckConnectionState(CancellationToken token)
    {
        using (PushProperties("CheckConnectionState"))
        {
            if (!IsClientConnected() || ConnectionState == ConnectionState.Error)
            {
                ConnectionState = ConnectionState.Disconnected;
                var stream = _stream;
                _stream = null;
                if (stream != null)
                {
                    stream.ErrorOccurred -= ClientOnErrorOccurred;
                    await stream.DisposeAsync();
                }
                await BackOffAfterShortSessions(token);
                try
                {
                    ConnectionState = ConnectionState.Connecting;
                    _client.Dispose();
                    _client = new SshClient(CreateConnectionInfo());
                
                    await _client.ConnectAsync(token);
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                    await CreateStream(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    LogDebug("The connection attempt was cancelled, the connection worker was stopped");
                    if (IsClientConnected())
                        _client.Disconnect();
                    ConnectionState = ConnectionState.Disconnected;
                }
                catch (Exception e)
                {
                    LogException(e);
                    ReportConnectionFailure(DescribeSshConnectionError(e));
                    ConnectionState = ConnectionState.Disconnected;
                    await Task.Delay(RetryWaitAfter(e), token);
                }
            }
            else if (IsClientConnected() && _stream == null)
            {
                try
                {
                    LogWarning("Client connected but stream is null, recreating stream");
                    // A new stream is a new remote CLI session; drop out of Connected first so
                    // the transition back fires ConnectionStateHandlers and subscribers re-initialise.
                    ConnectionState = ConnectionState.Connecting;
                    await CreateStream(token);
                }
                catch (Exception e) when (token.IsCancellationRequested)
                {
                    LogDebug("The stream was not recreated, the connection worker was stopped: {ExceptionMessage}", e.Message);
                    ConnectionState = ConnectionState.Disconnected;
                }
                catch (Exception e)
                {
                    LogException(e, "Failed to recreate stream");
                    ConnectionState = ConnectionState.Error;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
    }

    protected override async Task ProcessSendQueue(CancellationToken token)
    {
        var stream = _stream;
        if (!IsClientConnected() || stream is not { CanWrite: true } || ConnectionState == ConnectionState.Error)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            return;
        }

        while (TakeQueued() is { } item)
        {
            using (Trigger.Resume(item.TriggerId))
            using (PushProperties("ProcessSendQueue"))
            {
                var age = (DateTimeOffset.UtcNow - item.Timestamp).TotalSeconds;
                if (age >= QueueTimeout)
                {
                    LogWarning(
                        "Dropping queued message due to timeout. Age: {Age}s, Timeout: {Timeout}s, Message: {Message}",
                        age, QueueTimeout, item.Payload.Text);
                    continue;
                }

                try
                {
                    Write(stream, item.Payload.Text, item.Payload.Bytes);
                }
                catch (Exception e)
                {
                    LogWarning("Writing the queued message failed, it stays first in the queue: {ExceptionMessage}", e.Message);
                    lock (_sendQueue)
                        _sendQueue.AddFirst(item with { Timestamp = DateTimeOffset.UtcNow });
                    ConnectionState = ConnectionState.Error;
                    break;
                }
                InvokeRequestHandlers(item.Payload.Text);
                if (item.Payload.Bytes != null)
                    InvokeRequestHandlers(item.Payload.Bytes);
            }
        }

        await Task.Delay(TimeSpan.FromMilliseconds(500), token);
    }

    private async Task<ShellStream> CreateStream(CancellationToken token)
    {
        await ReceiveThreadWorker.Stop();
        var oldStream = _stream;
        _stream = null;
        if (oldStream != null)
        {
            oldStream.ErrorOccurred -= ClientOnErrorOccurred;
            await oldStream.DisposeAsync();
        }
        LogDebug("Creating new shell stream");
        var stream = _client.CreateShellStream("dumb", 1000, 1000, 1500, 1000, 8191, _modes);
        stream.ErrorOccurred += ClientOnErrorOccurred;
        _stream = stream;
        await Task.Delay(200, token);
        lock (_sessionLock)
        {
            token.ThrowIfCancellationRequested();
            ReceiveThreadWorker.Restart();
            ConnectionState = ConnectionState.Connected;
            _sessionStartedAt = Stopwatch.GetTimestamp();
        }
        LogInformation("Shell stream created successfully");
        return stream;
    }

    private async Task BackOffAfterShortSessions(CancellationToken token)
    {
        if (_sessionStartedAt is not { } startedAt)
            return;
        _sessionStartedAt = null;

        if (Stopwatch.GetElapsedTime(startedAt) >= ShortSession)
        {
            _consecutiveShortSessions = 0;
            return;
        }

        _consecutiveShortSessions++;
        if (_consecutiveShortSessions < 2)
            return;

        var delay = TimeSpan.FromSeconds(Math.Min(60, 5 << Math.Min(_consecutiveShortSessions - 2, 4)));
        LogWarning("The SSH session has dropped within {ShortSession} of connecting {Count} times in a row, waiting {Delay} before reconnecting",
            ShortSession, _consecutiveShortSessions, delay);
        await Task.Delay(delay, token);
    }

    private string DescribeSshConnectionError(Exception e) => e switch
    {
        SshAuthenticationException => $"SSH authentication to {Host} was rejected for user {_username}; the next attempt is in {RejectedCredentialsWait.TotalMinutes} minutes",
        SshOperationTimeoutException => $"The SSH connection to {Host}:{Port} timed out",
        SshConnectionException { InnerException: SocketException inner } => DescribeConnectionError(inner),
        SshConnectionException s => $"The SSH connection to {Host}:{Port} failed: {s.Message}",
        _ => DescribeConnectionError(e)
    };

    private static TimeSpan RetryWaitAfter(Exception e) =>
        e is SshAuthenticationException ? RejectedCredentialsWait : FailureWait;

    private void AuthenticationMethodOnAuthenticationPrompt(object? sender, AuthenticationPromptEventArgs e)
    {
        foreach (AuthenticationPrompt prompt in e.Prompts)
        {
            prompt.Response = _password;
        }
    }

    private void ClientOnErrorOccurred(object? sender, ExceptionEventArgs e)
    {
        using (Trigger.Begin())
        using (PushProperties("ClientOnErrorOccurred"))
        {
            LogException(e.Exception, "SSH.Net has logged an error");
            ConnectionState = ConnectionState.Error;
        }
    }

    public override void Send(string message) => Send(message, null);

    public override void Send(byte[] bytes) => Send(Encoding.UTF8.GetString(bytes), bytes);

    private void Send(string message, byte[]? bytes)
    {
        using (PushProperties("Send"))
        {
            if (DesiredConnectionState == ConnectionState.Disconnected)
            {
                LogDebug("Not sending, the client was disconnected on request");
                return;
            }

            var stream = _stream;
            if (!IsClientConnected() || stream is not {CanWrite: true})
            {
                LogDebug("Cannot send, not connected or stream unavailable. Queueing message.");
                EnqueueWithCap(message, bytes);
                return;
            }

            lock (_sendQueue)
            {
                if (_sendQueue.Count > 0)
                {
                    EnqueueWithCap(message, bytes);
                    return;
                }
            }

            try
            {
                Write(stream, message, bytes);
            }
            catch (Exception e)
            {
                LogWarning("Send failed, queueing the message: {ExceptionMessage}", e.Message);
                EnqueueWithCap(message, bytes);
                ConnectionState = ConnectionState.Error;
                return;
            }
            InvokeRequestHandlers(message);
            if (bytes != null)
                InvokeRequestHandlers(bytes);
        }
    }

    private static void Write(ShellStream stream, string message, byte[]? bytes)
    {
        if (bytes == null)
        {
            stream.Write(message);
            return;
        }
        stream.Write(bytes);
        stream.Flush();
    }

    private bool IsClientConnected()
    {
        try
        {
            return _client.IsConnected;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private QueuedPayload<(string Text, byte[]? Bytes)>? TakeQueued()
    {
        lock (_sendQueue)
        {
            if (_sendQueue.First is not { } first)
                return null;
            _sendQueue.RemoveFirst();
            return first.Value;
        }
    }

    private void EnqueueWithCap(string message, byte[]? bytes)
    {
        lock (_sendQueue)
        {
            if (_sendQueue.Count >= MaxQueueSize && _sendQueue.First is { } dropped)
            {
                _sendQueue.RemoveFirst();
                using (Trigger.Resume(dropped.Value.TriggerId))
                using (PushProperties("EnqueueWithCap"))
                    LogWarning("Send queue full, dropping oldest message. MaxQueueSize: {MaxQueueSize}", MaxQueueSize);
            }
            _sendQueue.AddLast(new QueuedPayload<(string Text, byte[]? Bytes)>(DateTimeOffset.UtcNow, (message, bytes), Trigger.Id));
        }
    }

    public override void Connect()
    {
        DesiredConnectionState = ConnectionState.Connected;
        AddEvent(EventType.Connection, "Connect requested");
        lock (_sendQueue)
            _sendQueue.Clear();
        _consecutiveShortSessions = 0;
        ConnectionStateWorker.Restart();
        ReceiveThreadWorker.Restart();
        SendQueueWorker.Restart();
    }

    public override void Reconnect()
    {
        if (DesiredConnectionState == ConnectionState.Disconnected)
        {
            using (PushProperties("Reconnect"))
                LogDebug("Not reconnecting, the client was disconnected on request");
            return;
        }

        _consecutiveShortSessions = 0;
        try
        {
            ConnectionState = ConnectionState.Disconnecting;

            // Swap the stream out before disposing it: the receive loop works on
            // a local copy and handles disposal, so there is no need to stop the
            // worker (and block on its in-flight iteration) from here.
            var oldStream = _stream;
            _stream = null;
            if (oldStream != null)
            {
                oldStream.ErrorOccurred -= ClientOnErrorOccurred;
                oldStream.Dispose();
            }

            if (IsClientConnected())
                _client.Disconnect();

            _client.Dispose();
            _client = new SshClient(CreateConnectionInfo());

            ConnectionState = ConnectionState.Disconnected;
        }
        catch (Exception e)
        {
            LogException(e);
            ConnectionState = ConnectionState.Error;
        }

        lock (_sessionLock)
        {
            if (DesiredConnectionState != ConnectionState.Disconnected)
                ConnectionStateWorker.Restart();
        }
    }

    public override void Disconnect()
    {
        DesiredConnectionState = ConnectionState.Disconnected;
        AddEvent(EventType.Connection, "Disconnect requested");
        lock (_sessionLock)
        {
            ConnectionStateWorker.Stop();
            ReceiveThreadWorker.Stop();
        }
        SendQueueWorker.Stop();
        ConnectionState = ConnectionState.Disconnecting;
        _sessionStartedAt = null;

        var stream = _stream;
        _stream = null;
        if (stream != null)
        {
            stream.ErrorOccurred -= ClientOnErrorOccurred;
            stream.Dispose();
        }

        if (IsClientConnected())
            _client.Disconnect();
        lock (_sendQueue)
            _sendQueue.Clear();
        ClearConnectionFailures();
        ConnectionState = ConnectionState.Disconnected;
    }
}