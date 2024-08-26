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

    public AvCodersSshClient(string host, ushort port, string username, string password)
        : base(host, port)
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

    protected override void Receive()
    {
        if (_client.IsConnected && _stream is { CanRead: true })
        {
            while (_stream.Length > 0)
            {
                ResponseHandlers?.Invoke(_stream.ReadLine() ?? string.Empty);
            }
            Thread.Sleep(50);
        }
        else
        {
            Log("Client not connected or stream not ready, not reading");
            Thread.Sleep(5000);
        }
    }

    protected override void CheckConnectionState()
    {
        if (!_client.IsConnected)
        {
            UpdateConnectionState(ConnectionState.Disconnected);
            _stream?.Dispose();
            Log("Reconnecting to device");
            try
            {
                _client.Connect();
                CreateStream();
            }
            catch (SshOperationTimeoutException e)
            {
                Log($"{Host} - The operation timed out\r\n{e.Message}", EventLevel.Error);
                Log(e.StackTrace ?? "No stack trace available", EventLevel.Error);
                UpdateConnectionState(ConnectionState.Disconnected);
            }
            catch (SshAuthenticationException e)
            {
                Log($"{Host} - Authentication Error\r\n{e.Message}", EventLevel.Error);
                Log(e.StackTrace ?? "No stack trace available", EventLevel.Error);
                UpdateConnectionState(ConnectionState.Disconnected);
            }
            catch (SshConnectionException e)
            {
                Log($"{Host} - The connection could not be established\r\n{e.Message}", EventLevel.Error);
                Log(e.StackTrace ?? "No stack trace available", EventLevel.Error);
                UpdateConnectionState(ConnectionState.Disconnected);
            }
            catch (ObjectDisposedException e)
            {
                Log($"{Host} - Object disposed exception\r\n{e}", EventLevel.Error);
                Log(e.StackTrace ?? "No stack trace available", EventLevel.Error);
                UpdateConnectionState(ConnectionState.Disconnected);
            }
            catch (InvalidOperationException e)
            {
                Log($"{Host} - InvalidOperationException - {e.Message}", EventLevel.Error);
                Log(e.StackTrace ?? "No stack trace available", EventLevel.Error);
                UpdateConnectionState(ConnectionState.Connected);
            }
            catch (SocketException e)
            {
                Log($"{Host} - Socket exception\r\n{e}", EventLevel.Error);
                Log(e.StackTrace ?? "No stack trace available", EventLevel.Error);
                UpdateConnectionState(ConnectionState.Disconnected);
            }
            catch (ProxyException e)
            {
                Log($"{Host} - Proxy exception\r\n{e}", EventLevel.Error);
                Log(e.StackTrace ?? "No stack trace available", EventLevel.Error);
                UpdateConnectionState(ConnectionState.Disconnected);
            }
            catch (Exception e)
            {
                Log($"{Host} - Unexpected exception\r\n{e}", EventLevel.Error);
                Log(e.StackTrace ?? "No stack trace available", EventLevel.Error);
                UpdateConnectionState(ConnectionState.Disconnected);
            }
            Thread.Sleep(32000);
        }
        else
        {
            if (_stream == null)
                CreateStream();

            try
            {
                bool? test = _stream?.CanRead;
            }
            catch (ObjectDisposedException)
            {
                CreateStream();
            }

            Thread.Sleep(5000);
        }
    }

    protected override void ProcessSendQueue()
    {
        if (!_client.IsConnected || _stream is { CanWrite: true })
            Thread.Sleep(1000);
        else
        {
            while (_sendQueue.Count > 0)
            {
                var item = _sendQueue.Dequeue();
                if (Math.Abs((DateTime.Now - item.Timestamp).TotalSeconds) < QueueTimeout)
                    _stream!.Write(item.Payload);
            }
            Thread.Sleep(1100);
        }
    }

    private void CreateStream()
    {
        Log("Recreating stream");
        _stream = _client.CreateShellStream("response", 1000, 1000, 1500, 1000, 8191, _modes);
        _stream!.ErrorOccurred += ClientOnErrorOccurred;
        Thread.Sleep(1000);
        UpdateConnectionState(ConnectionState.Connected);
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
    }

    public override void Send(string message)
    {
        if (_client.IsConnected)
            _stream!.Write(message);
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