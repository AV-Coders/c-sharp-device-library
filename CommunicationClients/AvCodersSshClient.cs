using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
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
        UpdateConnectionState(ConnectionState.Unknown);
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
        Log("Receive loop start");
        if (_client.IsConnected)
        {
            Log("Ready to receive messages...");
            using var reader = new StreamReader(_stream!);
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();
                if(line != null)
                    InvokeResponseHandlers(line);
            }
        }
        else
        {
            Log("Client not connected or stream not ready, not reading");
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
        Log("Receive loop end");
    }

    protected override async Task CheckConnectionState(CancellationToken token)
    {
        Log("Checking connection state...");
        if (!_client.IsConnected)
        {
            Log("Reconnecting to device");
            UpdateConnectionState(ConnectionState.Disconnected);
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
                Log($"{Host} - {e.GetType().Name} - {e.Message}", EventLevel.Error);
                Log(e.StackTrace ?? "No stack trace available", EventLevel.Error);
                UpdateConnectionState(ConnectionState.Disconnected);
            }
            catch (Exception e)
            {
                Log($"{Host} - Unexpected exception - {e.GetType().Name}\r\n{e}", EventLevel.Error);
                Log(e.StackTrace ?? "No stack trace available", EventLevel.Error);
                UpdateConnectionState(ConnectionState.Disconnected);
            }

            await Task.Delay(TimeSpan.FromSeconds(60), token);
        }
        await Task.Delay(TimeSpan.FromSeconds(5), token);
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
        Log("Recreating stream");
        await ReceiveThreadWorker.Stop();
        _stream = _client.CreateShellStream("response", 1000, 1000, 1500, 1000, 8191, _modes);
        _stream.ErrorOccurred += ClientOnErrorOccurred;
        await Task.Delay(200, token);
        ReceiveThreadWorker.Restart();
        UpdateConnectionState(ConnectionState.Connected);
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
        Log($"An error has occurred with the stream: \r\n{e.Exception.Message}", EventLevel.Error);
        Log(e.Exception.StackTrace ?? "No stack trace available", EventLevel.Error);
        UpdateConnectionState(ConnectionState.Error);
    }

    public override void Send(string message)
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
                Log("Send failed, stream was disposed.  Recreating stream and queueing message");
                _ = CreateStream(new CancellationToken());
                _sendQueue.Enqueue(new QueuedPayload<string>(DateTime.Now, message));
            }
            catch (NullReferenceException)
            {
                Log("Send failed, stream has not yet been created. Waiting for the connection flow to continue");
                _sendQueue.Enqueue(new QueuedPayload<string>(DateTime.Now, message));
            }
        }
            
        else
            _sendQueue.Enqueue(new QueuedPayload<string>(DateTime.Now, message));
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
        Log($"Reconnecting");
        UpdateConnectionState(ConnectionState.Disconnecting);
        ReceiveThreadWorker.Stop();
        _stream?.Dispose();
        _client.Disconnect();
        UpdateConnectionState(ConnectionState.Disconnected);
        // The worker will handle reconnection
    }

    public override void Disconnect()
    {
        Log($"Disconnecting");
        ConnectionStateWorker.Stop();
        UpdateConnectionState(ConnectionState.Disconnecting);
        _client.Disconnect();
        UpdateConnectionState(ConnectionState.Disconnected);
    }
}