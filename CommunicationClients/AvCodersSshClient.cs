using System.Collections.Concurrent;
using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using Serilog;
using SshClient = Renci.SshNet.SshClient;
using SshClientBase = AVCoders.Core.SshClient;

namespace AVCoders.CommunicationClients;

public class AvCodersSshClient : SshClientBase
{
    private readonly string _username;
    private readonly string _password;
    private readonly ConcurrentQueue<QueuedPayload<string>> _sendQueue = new();

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
        using (PushProperties("Receive"))
        {
            while (!token.IsCancellationRequested)
            {
                if (_client.IsConnected && _stream != null)
                {
                    try
                    {
                        if (_stream.DataAvailable)
                        {
                            var line = _stream.ReadLine();
                            if (!string.IsNullOrEmpty(line))
                                InvokeResponseHandlers(line);
                        }
                        else
                        {
                            await Task.Delay(200, token);
                        }
                    }
                    catch (Exception e) when (e is ObjectDisposedException)
                    {
                        Log.Warning("Stream was disposed, waiting for reconnection");
                        await Task.Delay(TimeSpan.FromSeconds(5), token);
                    }
                }
                else
                {
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
                catch (Exception e) when (e is SshOperationTimeoutException 
                                              or SshAuthenticationException 
                                              or SshConnectionException 
                                              or ObjectDisposedException 
                                              or InvalidOperationException 
                                              or SocketException 
                                              or ProxyException)
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
            else if (_client.IsConnected && _stream == null)
            {
                try
                {
                    Log.Warning("Client connected but stream is null, recreating stream");
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
        if (!_client.IsConnected || _stream is not { CanWrite: true })
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        else
        {
            using (PushProperties("ProcessSendQueue"))
            {
                while (_sendQueue.TryDequeue(out var item))
                {
                    var age = (DateTime.Now - item.Timestamp).TotalSeconds;
                    if (age >= QueueTimeout)
                    {
                        Log.Warning(
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

    private async Task<ShellStream> CreateStream(CancellationToken token)
    {
        await ReceiveThreadWorker.Stop();
        if (_stream != null)
        {
            _stream.ErrorOccurred -= ClientOnErrorOccurred;
            await _stream.DisposeAsync();
            _stream = null;
        }
        Log.Debug("Creating new shell stream");
        _stream = _client.CreateShellStream("dumb", 1000, 1000, 1500, 1000, 8191, _modes);
        _stream.ErrorOccurred += ClientOnErrorOccurred;
        await Task.Delay(200, token);
        ReceiveThreadWorker.Restart();
        ConnectionState = ConnectionState.Connected;
        Log.Information("Shell stream created successfully");
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
            if (_client.IsConnected && _stream is {CanWrite: true})
            {
                try
                {
                    _stream.Write(message);
                    InvokeRequestHandlers(message);
                }
                catch (ObjectDisposedException e)
                {
                    Log.Debug("Send failed, stream was disposed. Queueing message. {ExceptionMessage}", e.Message);
                    _sendQueue.Enqueue(new QueuedPayload<string>(DateTime.Now, message));
                    ConnectionState = ConnectionState.Error;
                }
            }
            else
            {
                Log.Debug("Cannot send, not connected or stream unavailable. Queueing message.");
                _sendQueue.Enqueue(new QueuedPayload<string>(DateTime.Now, message));
                ConnectionState = ConnectionState.Error;
            }
        }
    }

    public override void Send(byte[] bytes)
    {
        var message = System.Text.Encoding.UTF8.GetString(bytes);
        Send(message);
        InvokeRequestHandlers(bytes);
    }

    public override void Connect() => ConnectionStateWorker.Restart();

    public override void Reconnect()
    {
        try
        {
            ConnectionState = ConnectionState.Disconnecting;
            ReceiveThreadWorker.Stop().Wait();
            _stream?.Dispose();
            
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
        ConnectionStateWorker.Stop();
        ConnectionState = ConnectionState.Disconnecting;
        
        _stream?.Dispose();
        
        if (_client.IsConnected)
            _client.Disconnect();
        ConnectionState = ConnectionState.Disconnected;
    }
}