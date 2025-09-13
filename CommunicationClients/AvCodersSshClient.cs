using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using Serilog;
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
        using (LogContext.PushProperty(MethodProperty, "Receive"))
        {
            if (_client.IsConnected)
            {
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
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
        }
    }

    protected override async Task CheckConnectionState(CancellationToken token)
    {
        using (LogContext.PushProperty(MethodProperty, "CheckConnectionState"))
        {
            if (!_client.IsConnected)
            {
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
        LogException(e.Exception);
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
                catch (ObjectDisposedException e)
                {
                    Log.Debug("Send failed, stream was disposed.  Recreating stream and queueing message. {ExceptionMessage}", e.Message);
                    _ = CreateStream(CancellationToken.None);
                    _sendQueue.Enqueue(new QueuedPayload<string>(DateTime.Now, message));
                }
                catch (NullReferenceException e)
                {
                    Log.Debug("Send failed, stream has not yet been created. Waiting for the connection flow to continue. {ExceptionMessage}", e.Message);
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

    public override void Connect() => ConnectionStateWorker.Restart();

    public override void Reconnect()
    {
        ConnectionState = ConnectionState.Disconnecting;
        ReceiveThreadWorker.Stop();
        _stream?.Dispose();
        _client.Disconnect();
        ConnectionState = ConnectionState.Disconnected;
        // The worker will handle reconnection
    }

    public override void Disconnect()
    {
        ConnectionStateWorker.Stop();
        ConnectionState = ConnectionState.Disconnecting;
        _client.Disconnect();
        ConnectionState = ConnectionState.Disconnected;
    }
}