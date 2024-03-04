using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace AVCoders.CommunicationClients;

public class AvCodersSshClient : IpComms
{
    private readonly string _username;
    private readonly string _password;

    private SshClient _client;
    private ConnectionInfo _connectionInfo;

    private readonly ConnectionType _connectionType;
    private Thread? _responseThread;
    private Dictionary<TerminalModes, uint> _modes;
    private ShellStream? _stream;

    public AvCodersSshClient(string host, ushort port, string username, string password, ConnectionType connectionType)
        : base(host, port)
    {
        _username = username;
        _password = password;
        _connectionType = connectionType;
        ConnectionState = ConnectionState.Unknown;

        _connectionInfo = CreateConnectionInfo();
        _modes = new Dictionary<TerminalModes, uint> { {TerminalModes.ECHO, 0} };
        new Thread(_ =>
        {
            CreateNewActiveClient();
            CreateConnectionMonitoringThread();
            CreateResponseThread();
        }).Start();
    }

    private ConnectionInfo CreateConnectionInfo()
    {
        KeyboardInteractiveAuthenticationMethod authenticationMethod =
            new KeyboardInteractiveAuthenticationMethod(_username);
        authenticationMethod.AuthenticationPrompt += AuthenticationMethodOnAuthenticationPrompt;
        return new ConnectionInfo(Host, Port, _username, authenticationMethod);
    }

    private void CreateConnectionMonitoringThread()
    {
        new Thread(() =>
        {
            while (_client == null)
            {
                Thread.Sleep(1000);
            }
            
            while (_connectionType == ConnectionType.Persistent)
            {
                if (!_client.IsConnected)
                {
                    UpdateConnectionState(ConnectionState.Disconnected);
                    _stream?.Dispose();
                    LogHandlers?.Invoke("Reconnecting to device");
                    ConnectToClient();
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
                    catch (ObjectDisposedException e)
                    {
                        CreateStream();
                    }

                    Thread.Sleep(5000);
                }
            }
        }).Start();
    }

    private void CreateStream()
    {
        LogHandlers?.Invoke("Recreating stream");
        _stream = _client.CreateShellStream("response", 1000, 1000, 1500, 1000, 8191, _modes);
        _stream.ReadTimeout = 5000;
        _stream!.ErrorOccurred += ClientOnErrorOccurred;
        Thread.Sleep(300);
        UpdateConnectionState(ConnectionState.Connected);
    }

    private void CreateResponseThread()
    {
        if (_connectionType == ConnectionType.Persistent)
        {
            LogHandlers?.Invoke("Creating response thread");
            _responseThread = new Thread(() =>
            {
                LogHandlers?.Invoke("Starting response thread");

                while (_connectionType == ConnectionType.Persistent)
                {
                    if (_client.IsConnected && _stream != null)
                    {
                        if (_stream.CanRead)
                        {
                            while (_stream.Length > 0)
                            {
                                ResponseHandlers?.Invoke(_stream.ReadLine());
                            }
                            Thread.Sleep(150);
                        }
                        else
                        {
                            LogHandlers?.Invoke("Stream cannot read");
                        }
                    }
                    else
                    {
                        LogHandlers?.Invoke("Client not connected, not reading");
                        Thread.Sleep(700);
                    }
                }
            });
            _responseThread.Start();
        }
    }

    private void CreateNewActiveClient()
    {
        try
        {
            Dispose();
            _client = new SshClient(_connectionInfo);
            _client.ErrorOccurred += ClientOnErrorOccurred;
            ConnectToClient();
        }
        catch (Exception e)
        {
            LogHandlers?.Invoke($"{Host} - Uncaught exception\n{e}", EventLevel.Error);
            Console.WriteLine(e);
        }
    }

    private void ConnectToClient()
    {
        try
        {
            _client.Connect();
            CreateStream();
        }
        catch (SshOperationTimeoutException e)
        {
            LogHandlers?.Invoke($"{Host} - The operation timed out\n{e.Message}", EventLevel.Error);
            UpdateConnectionState(ConnectionState.Disconnected);
        }
        catch (SshAuthenticationException e)
        {
            LogHandlers?.Invoke($"{Host} - Authentication Error\n{e.Message}", EventLevel.Error);
            UpdateConnectionState(ConnectionState.Disconnected);
        }
        catch (SshConnectionException e)
        {
            LogHandlers?.Invoke($"{Host} - The connection could not be established\n{e.Message}", EventLevel.Error);
            UpdateConnectionState(ConnectionState.Disconnected);
        }
        catch (ObjectDisposedException e)
        {
            LogHandlers?.Invoke($"{Host} - Object disposed exception\n{e}", EventLevel.Error);
            UpdateConnectionState(ConnectionState.Disconnected);
        }
        catch (InvalidOperationException)
        {
            LogHandlers?.Invoke($"{Host} - Is already connected", EventLevel.Error);
            UpdateConnectionState(ConnectionState.Connected);
        }
        catch (SocketException e)
        {
            LogHandlers?.Invoke($"{Host} - Socket exception\n{e}", EventLevel.Error);
            UpdateConnectionState(ConnectionState.Disconnected);
        }
        catch (ProxyException e)
        {
            LogHandlers?.Invoke($"{Host} - Proxy exception\n{e}", EventLevel.Error);
            UpdateConnectionState(ConnectionState.Disconnected);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    private void AuthenticationMethodOnAuthenticationPrompt(object? sender, AuthenticationPromptEventArgs e)
    {
        foreach (AuthenticationPrompt prompt in e.Prompts)
        {
            prompt.Response = _password;
        }
    }

    private void ClientOnErrorOccurred(object sender, ExceptionEventArgs e)
    {
        LogHandlers?.Invoke($"An error has occurred with the stream: \r\n{e.Exception}", EventLevel.Error);
    }

    private void UpdateConnectionState(ConnectionState connectionState)
    {
        if (ConnectionState != connectionState)
        {
            ConnectionState = connectionState;
            ConnectionStateHandlers?.Invoke(connectionState);
        }
    }

    public override void Send(string message)
    {
        switch (_connectionType)
        {
            case ConnectionType.Persistent:
            {
                if (_client.IsConnected && _stream is { CanWrite: true })
                    _stream.WriteLine(message);
                else
                    LogHandlers?.Invoke($"Unable to send {message}, stream is either null or can't write",
                        EventLevel.Critical);

                break;
            }
            case ConnectionType.ShortLived:
            {
                if (!_client.IsConnected)
                    ConnectToClient();
                SshCommand response = _client.RunCommand(message);
                ResponseHandlers?.Invoke(response.Result);
                break;
            }
        }
    }

    public override void Send(byte[] bytes)
    {
        switch (_connectionType)
        {
            case ConnectionType.Persistent:
            {
                if (_stream is { CanWrite: true })
                    _stream.WriteLine(bytes.ToString());
                else
                    LogHandlers?.Invoke($"Unable to send {bytes}, stream is either null or can't write",
                        EventLevel.Critical);

                break;
            }
            case ConnectionType.ShortLived:
            {
                if (!_client.IsConnected)
                    ConnectToClient();
                SshCommand response = _client.RunCommand(bytes.ToString());
                ResponseHandlers?.Invoke(response.Result);
                break;
            }
        }
    }

    public override void SetPort(ushort port)
    {
        Port = port;
        _connectionInfo = CreateConnectionInfo();
        CreateNewActiveClient();
    }

    public override void SetHost(string host)
    {
        Host = host;
        _connectionInfo = CreateConnectionInfo();
        CreateNewActiveClient();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _stream?.Dispose();
    }
}