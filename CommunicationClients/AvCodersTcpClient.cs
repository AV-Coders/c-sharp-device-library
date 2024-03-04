using System.Net.Sockets;
using System.Text;
using Core_TcpClient = AVCoders.Core.TcpClient;

namespace AVCoders.CommunicationClients;

public class AvCodersTcpClient : Core_TcpClient
{
    private System.Net.Sockets.TcpClient _client;
    private readonly ConnectionType _connectionType;
    private List<Byte[]> _sendQueue = new();
    private Thread? _receiveThread;

    public AvCodersTcpClient(string host, ushort port = 23, ConnectionType connectionType = ConnectionType.Persistent) :
        base(host, port)
    {
        _connectionType = connectionType;
        new Thread(_=> ConnectToClient()).Start();
    }

    private void Receive()
    {
        while (_client.Connected)
        {
            try
            {
                byte[] buffer = new byte[1024];
                var bytesRead = _client.GetStream().Read(buffer, 0, buffer.Length);

                if (bytesRead > 0)
                {
                    string response = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                    ResponseHandlers?.Invoke(response);
                }
            }
            catch (IOException e)
            {
                Error($"Receive - IOException:\n{e}");
                UpdateConnectionState(ConnectionState.Disconnected);
            }
            catch (ObjectDisposedException e)
            {
                Error($"Receive  - ObjectDisposedException\n{e}");
                UpdateConnectionState(ConnectionState.Disconnected);
            }
            catch (Exception e)
            {
                Error($"Receive  - Exception:\n{e}");
                UpdateConnectionState(ConnectionState.Disconnected);
            }
            Thread.Sleep(TimeSpan.FromMilliseconds(30));
        }

        Log("Receive - Ending - Client disconnected");
    }

    private void ConnectToClient()
    {
        UpdateConnectionState(ConnectionState.Connecting);
        Log("Connecting to client");
        try
        {
            Log("ConnectToClient - Creating object");
            _client = new System.Net.Sockets.TcpClient(Host, Port);
            UpdateConnectionState(ConnectionState.Connected);
            ProcessSendQueue();

            _receiveThread = new Thread(Receive);
            _receiveThread.Start();
        }
        catch (SocketException e)
        {
            Error($"ConnectToClient - Socket exception - {e.Message}");
            UpdateConnectionState(ConnectionState.Error);
        }
        catch (ObjectDisposedException e)
        {
            Error($"ConnectToClient - Object disposed exception - {e.Message}");
        }
    }

    private void ProcessSendQueue()
    {
        foreach (byte[] message in _sendQueue.ToList())
        {
            _client.GetStream().Write(message);
            _sendQueue.Remove(message);
        }
    }

    private void RecreateClient()
    {
        Log($"Recreating client");
        UpdateConnectionState(ConnectionState.Disconnecting);
        _client.Close();
        if (_connectionType == ConnectionType.Persistent)
        {
            UpdateConnectionState(ConnectionState.Disconnected);
            new Thread(ConnectToClient).Start();
        }
        else
        {
            UpdateConnectionState(ConnectionState.Idle);
        }
    }

    public override void Send(string message)
    {
        byte[] bytes = Bytes.FromString(message);
        Send(bytes);
    }

    public override void Send(byte[] bytes)
    {
        try
        {
            if (ConnectionState == ConnectionState.Connecting)
            {
                _sendQueue.Add(bytes);
            }
            else
            {
                if (!_client.Connected)
                {
                    ConnectToClient();
                }

                SendAsync(bytes);
            }
        }
        catch (InvalidOperationException e)
        {
            Error($"Send - InvalidOperationException - {e.Message}");
        }
        catch (IOException e)
        {
            Error($"Send - IOException - {e.Message}");
        }
        catch (Exception e)
        {
            Error($"Send - Unhandled exception - {e.Message}");
        }
    }

    private async Task SendAsync(byte[] data)
    {
        await _client.GetStream().WriteAsync(data);
    }

    public override void SetPort(ushort port)
    {
        Log($"Setting port to {port}");
        Port = port;
        new Thread(() =>
        {
            while (_client == null)
            {
                Thread.Sleep(1000);
            }
            RecreateClient();
        }).Start();
        
    }

    public override void SetHost(string host)
    {
        Log($"Setting host to {host}");
        Host = host;
        RecreateClient();
    }

    private void Log(string message)
    {
        LogHandlers?.Invoke($"TCP Client for {Host}:{Port} - {message}");
    }

    private void Error(string message)
    {
        LogHandlers?.Invoke($"TCP Client for {Host}:{Port} - {message}", EventLevel.Error);
    }

    private void UpdateConnectionState(ConnectionState connectionState)
    {
        ConnectionState = connectionState;
        ConnectionStateHandlers?.Invoke(connectionState);
    }
}