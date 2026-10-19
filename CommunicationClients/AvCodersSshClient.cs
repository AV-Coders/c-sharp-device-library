using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
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
    private readonly string _username;
    private readonly string _password;
    private readonly ConcurrentQueue<QueuedPayload<string>> _sendQueue = new();

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
        KeyboardInteractiveAuthenticationMethod authenticationMethod =
            new KeyboardInteractiveAuthenticationMethod(_username);
        authenticationMethod.AuthenticationPrompt += AuthenticationMethodOnAuthenticationPrompt;
        return new ConnectionInfo(Host, Port, _username, authenticationMethod);
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
                                InvokeResponseHandlers(line);
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
            if (!_client.IsConnected || ConnectionState == ConnectionState.Error)
            {
                ConnectionState = ConnectionState.Disconnected;
                if (_stream != null)
                {
                    _stream.ErrorOccurred -= ClientOnErrorOccurred;
                    await _stream.DisposeAsync();
                    _stream = null;
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
                    ConnectionState = ConnectionState.Connected;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    LogDebug("The connection attempt was cancelled, the connection worker was stopped");
                    if (_client.IsConnected)
                        _client.Disconnect();
                    ConnectionState = ConnectionState.Disconnected;
                }
                catch (Exception e)
                {
                    LogException(e);
                    ReportConnectionFailure(DescribeSshConnectionError(e));
                    ConnectionState = ConnectionState.Disconnected;
                    await Task.Delay(TimeSpan.FromSeconds(60), token);
                }
            }
            else if (_client.IsConnected && _stream == null)
            {
                try
                {
                    LogWarning("Client connected but stream is null, recreating stream");
                    // A new stream is a new remote CLI session; drop out of Connected first so
                    // the transition back fires ConnectionStateHandlers and subscribers re-initialise.
                    ConnectionState = ConnectionState.Connecting;
                    await CreateStream(token);
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
        try
        {
            if (!_client.IsConnected || _stream is not { CanWrite: true })
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            else
            {
                using (PushProperties("ProcessSendQueue"))
                {
                    while (_sendQueue.TryDequeue(out var item))
                    {
                        var age = (DateTimeOffset.UtcNow - item.Timestamp).TotalSeconds;
                        if (age >= QueueTimeout)
                        {
                            LogWarning(
                                "Dropping queued message due to timeout. Age: {Age}s, Timeout: {Timeout}s, Message: {Message}",
                                age, QueueTimeout, item.Payload);
                            continue;
                        }

                        _stream.Write(item.Payload);
                        InvokeRequestHandlers(item.Payload);
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(500), token);
                }
            }
        }
        catch (ObjectDisposedException)
        {
            LogWarning("Client was disposed during send queue processing, waiting for reconnection");
            await Task.Delay(TimeSpan.FromSeconds(3), token);
        }
    }

    private async Task<ShellStream> CreateStream(CancellationToken token)
    {
        await ReceiveThreadWorker.Stop();
        if (_stream != null)
        {
            _stream.ErrorOccurred -= ClientOnErrorOccurred;
            await _stream.DisposeAsync();
            _stream = null;
        }
        LogDebug("Creating new shell stream");
        _stream = _client.CreateShellStream("dumb", 1000, 1000, 1500, 1000, 8191, _modes);
        _stream.ErrorOccurred += ClientOnErrorOccurred;
        await Task.Delay(200, token);
        ReceiveThreadWorker.Restart();
        ConnectionState = ConnectionState.Connected;
        _sessionStartedAt = Stopwatch.GetTimestamp();
        LogInformation("Shell stream created successfully");
        return _stream;
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
        SshAuthenticationException => $"SSH authentication to {Host} was rejected for user {_username}",
        SshOperationTimeoutException => $"The SSH connection to {Host}:{Port} timed out",
        SshConnectionException { InnerException: SocketException inner } => DescribeConnectionError(inner),
        SshConnectionException s => $"The SSH connection to {Host}:{Port} failed: {s.Message}",
        _ => DescribeConnectionError(e)
    };

    private void AuthenticationMethodOnAuthenticationPrompt(object? sender, AuthenticationPromptEventArgs e)
    {
        foreach (AuthenticationPrompt prompt in e.Prompts)
        {
            prompt.Response = _password;
        }
    }

    private void ClientOnErrorOccurred(object? sender, ExceptionEventArgs e)
    {
        using (PushProperties("ClientOnErrorOccurred"))
        {
            LogException(e.Exception, "SSH.Net has logged an error");
            ConnectionState = ConnectionState.Error;
        }
    }

    public override void Send(string message)
    {
        using (PushProperties("Send"))
        {
            if (DesiredConnectionState == ConnectionState.Disconnected)
            {
                LogDebug("Not sending, the client was disconnected on request");
                return;
            }

            if (_client.IsConnected && _stream is {CanWrite: true})
            {
                try
                {
                    _stream.Write(message);
                    InvokeRequestHandlers(message);
                }
                catch (ObjectDisposedException e)
                {
                    LogDebug("Send failed, stream was disposed. Queueing message. {ExceptionMessage}", e.Message);
                    EnqueueWithCap(message);
                    ConnectionState = ConnectionState.Error;
                }
            }
            else
            {
                LogDebug("Cannot send, not connected or stream unavailable. Queueing message.");
                EnqueueWithCap(message);
                ConnectionState = ConnectionState.Error;
            }
        }
    }

    public override void Send(byte[] bytes)
    {
        if (DesiredConnectionState == ConnectionState.Disconnected)
        {
            using (PushProperties("Send"))
                LogDebug("Not sending, the client was disconnected on request");
            return;
        }
        var message = System.Text.Encoding.UTF8.GetString(bytes);
        Send(message);
        InvokeRequestHandlers(bytes);
    }

    private void EnqueueWithCap(string message)
    {
        if (_sendQueue.Count >= MaxQueueSize)
        {
            _sendQueue.TryDequeue(out _);
            using (PushProperties("EnqueueWithCap"))
                LogWarning("Send queue full, dropping oldest message. MaxQueueSize: {MaxQueueSize}", MaxQueueSize);
        }
        _sendQueue.Enqueue(new QueuedPayload<string>(DateTimeOffset.UtcNow, message));
    }

    public override void Connect()
    {
        DesiredConnectionState = ConnectionState.Connected;
        AddEvent(EventType.Connection, "Connect requested");
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

            if (_client.IsConnected)
                _client.Disconnect();

            _client.Dispose();
            _client = new SshClient(CreateConnectionInfo());

            ConnectionState = ConnectionState.Disconnected;
            // The worker will handle reconnection
        }
        catch (Exception e)
        {
            LogException(e);
            ConnectionState = ConnectionState.Error;
        }
    }

    public override void Disconnect()
    {
        DesiredConnectionState = ConnectionState.Disconnected;
        AddEvent(EventType.Connection, "Disconnect requested");
        ConnectionStateWorker.Stop();
        ReceiveThreadWorker.Stop();
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

        if (_client.IsConnected)
            _client.Disconnect();
        _sendQueue.Clear();
        ClearConnectionFailures();
        ConnectionState = ConnectionState.Disconnected;
    }
}