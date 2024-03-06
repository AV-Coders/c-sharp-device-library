using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace AVCoders.CommunicationClients;

public class AvCodersSshClient : IpComms
{
    private readonly string _username;
    private readonly string _password;
    private readonly Queue<string> _sendQueue = new();

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

    public override void Receive()
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

    public override void CheckConnectionState()
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
                Error($"{Host} - The operation timed out\r\n{e.Message}");
                Error(e.StackTrace ?? "No stack trace available");
                UpdateConnectionState(ConnectionState.Disconnected);
            }
            catch (SshAuthenticationException e)
            {
                Error($"{Host} - Authentication Error\r\n{e.Message}");
                Error(e.StackTrace ?? "No stack trace available");
                UpdateConnectionState(ConnectionState.Disconnected);
            }
            catch (SshConnectionException e)
            {
                Error($"{Host} - The connection could not be established\r\n{e.Message}");
                Error(e.StackTrace ?? "No stack trace available");
                UpdateConnectionState(ConnectionState.Disconnected);
            }
            catch (ObjectDisposedException e)
            {
                Error($"{Host} - Object disposed exception\r\n{e}");
                Error(e.StackTrace ?? "No stack trace available");
                UpdateConnectionState(ConnectionState.Disconnected);
            }
            catch (InvalidOperationException e)
            {
                Error($"{Host} - InvalidOperationException - {e.Message}");
                Error(e.StackTrace ?? "No stack trace available");
                UpdateConnectionState(ConnectionState.Connected);
            }
            catch (SocketException e)
            {
                Error($"{Host} - Socket exception\r\n{e}");
                Error(e.StackTrace ?? "No stack trace available");
                UpdateConnectionState(ConnectionState.Disconnected);
            }
            catch (ProxyException e)
            {
                Error($"{Host} - Proxy exception\r\n{e}");
                Error(e.StackTrace ?? "No stack trace available");
                UpdateConnectionState(ConnectionState.Disconnected);
            }
            catch (Exception e)
            {
                Error($"{Host} - Unexpected exception\r\n{e}");
                Error(e.StackTrace ?? "No stack trace available");
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

    public override void ProcessSendQueue()
    {
        if (!_client.IsConnected || _stream is { CanWrite: true })
            Thread.Sleep(1000);
        else
        {
            while (_sendQueue.Count > 0)
            {
                _stream!.Write(_sendQueue.Dequeue());
            }
            Thread.Sleep(20);
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
        Error($"An error has occurred with the stream: \r\n{e.Exception.Message}");
        Error(e.Exception.StackTrace ?? "No stack trace available");
    }

    public override void Send(string message)
    {
        if (_client.IsConnected)
            _stream!.Write(message);
        else
            _sendQueue.Enqueue(message);
    }

    public override void Send(byte[] bytes)
    {
        if (bytes.ToString() != null)
            Send(bytes.ToString());
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

    private new void Log(string message)
    {
        LogHandlers?.Invoke($"{DateTime.Now} - SSH Client for {Host}:{Port} - {message}");
    }

    private new void Error(string message)
    {
        LogHandlers?.Invoke($"{DateTime.Now} - SSH Client for {Host}:{Port} - {message}", EventLevel.Error);
    }
}