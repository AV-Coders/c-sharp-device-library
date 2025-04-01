using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using Serilog.Context;
using SshClient = Renci.SshNet.SshClient;
using SshClientBase = AVCoders.Core.SshClient;

namespace AVCoders.CommunicationClients;

public class AvCodersSshClient : SshClientBase
{
    private readonly string _username;
    private readonly string _password;
    private readonly Queue<QueuedPayload<string>> _sendQueue = new();

    private SshClient _client;
    
    private readonly Dictionary<TerminalModes, uint> _modes;
    private ShellStream? _stream;

    public AvCodersSshClient(string host, ushort port, string username, string password, string name = "")
        : base(host, port, name)
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
        using (LogContext.PushProperty(MethodProperty, "Receive"))
        {
            Debug("Receive loop start");
            if (_client.IsConnected)
            {
                Debug("Ready to receive messages...");
                using var reader = new StreamReader(_stream!);
                while (!token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (line != null)
                        InvokeResponseHandlers(line);
                }
            }
            else
            {
                Debug("Client not connected or stream not ready, not reading");
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }

            Debug("Receive loop end");
        }
    }

    protected override async Task CheckConnectionState(CancellationToken token)
    {
        using (LogContext.PushProperty(MethodProperty, "CheckConnectionState"))
        {
            Debug("Checking connection state...");
            if (!_client.IsConnected)
            {
                Debug("Reconnecting to device");
                ConnectionState = ConnectionState.Disconnected;
                if (_stream != null)
                    await _stream.DisposeAsync();
                try
                {
                    await _client.ConnectAsync(token);
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                    await CreateStream(token);
                }
                catch (Exception e) when (e is SshOperationTimeoutException ||
                                          e is SshAuthenticationException ||
                                          e is SshConnectionException ||
                                          e is ObjectDisposedException ||
                                          e is InvalidOperationException ||
                                          e is SocketException ||
                                          e is ProxyException)
                {
                    ConnectionState = ConnectionState.Disconnected;
                    LogException(e);
                }
                catch (Exception e)
                {
                    LogException(e);
                    ConnectionState = ConnectionState.Disconnected;
                }

                await Task.Delay(TimeSpan.FromSeconds(60), token);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
    }

    protected override async Task ProcessSendQueue(CancellationToken token)
    {
        if (!_client.IsConnected || _stream is { CanWrite: true })
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        else
        {
            while (_sendQueue.Count > 0)
            {
                var item = _sendQueue.Dequeue();
                if (Math.Abs((DateTime.Now - item.Timestamp).TotalSeconds) < QueueTimeout)
                {
                    _stream!.Write(item.Payload);
                    InvokeRequestHandlers(item.Payload);
                }
                
            }
            await Task.Delay(1100, token);
        }
    }

    private async Task<ShellStream> CreateStream(CancellationToken token)
    {
        Debug("Recreating stream");
        await ReceiveThreadWorker.Stop();
        _stream = _client.CreateShellStream("response", 1000, 1000, 1500, 1000, 8191, _modes);
        _stream.ErrorOccurred += ClientOnErrorOccurred;
        await Task.Delay(200, token);
        ReceiveThreadWorker.Restart();
        ConnectionState = ConnectionState.Connected;
        return _stream;
    }

    private void AuthenticationMethodOnAuthenticationPrompt(object? sender, AuthenticationPromptEventArgs e)
    {
        foreach (AuthenticationPrompt prompt in e.Prompts)
        {
            prompt.Response = _password;
        }
    }

    private void ClientOnErrorOccurred(object? sender, ExceptionEventArgs e)
    {
        Error($"An error has occurred with the stream: \r\n{e.Exception.Message}");
        Error(e.Exception.StackTrace ?? "No stack trace available");
        ConnectionState = ConnectionState.Error;
    }

    public override void Send(string message)
    {
        using (LogContext.PushProperty(MethodProperty, "Send"))
        {
            if (_client.IsConnected)
            {
                try
                {
                    _stream!.Write(message);
                    InvokeRequestHandlers(message);
                }
                catch (ObjectDisposedException)
                {
                    Debug("Send failed, stream was disposed.  Recreating stream and queueing message");
                    _ = CreateStream(new CancellationToken());
                    _sendQueue.Enqueue(new QueuedPayload<string>(DateTime.Now, message));
                }
                catch (NullReferenceException)
                {
                    Debug("Send failed, stream has not yet been created. Waiting for the connection flow to continue");
                    _sendQueue.Enqueue(new QueuedPayload<string>(DateTime.Now, message));
                }
            }

            else
                _sendQueue.Enqueue(new QueuedPayload<string>(DateTime.Now, message));
        }
    }

    public override void Send(byte[] bytes)
    {
        var message = bytes.ToString();
        if (message != null)
            Send(message);
    }

    public override void SetPort(ushort port)
    {
        Port = port;
        Disconnect();
        _client = new SshClient(CreateConnectionInfo());
        Connect();
    }

    public override void SetHost(string host)
    {
        Host = host;
        Disconnect();
        _client = new SshClient(CreateConnectionInfo());
        Connect();
    }

    public override void Connect() => ConnectionStateWorker.Restart();

    public override void Reconnect()
    {
        Debug($"Reconnecting");
        ConnectionState = ConnectionState.Disconnecting;
        ReceiveThreadWorker.Stop();
        _stream?.Dispose();
        _client.Disconnect();
        ConnectionState = ConnectionState.Disconnected;
        // The worker will handle reconnection
    }

    public override void Disconnect()
    {
        Debug($"Disconnecting");
        ConnectionStateWorker.Stop();
        ConnectionState = ConnectionState.Disconnecting;
        _client.Disconnect();
        ConnectionState = ConnectionState.Disconnected;
    }
}