using System.Net.Sockets;
using System.Text;
using Core_TcpClient = AVCoders.Core.TcpClient;
using TcpClient = System.Net.Sockets.TcpClient;

namespace AVCoders.CommunicationClients;

public class AvCodersTcpClient : Core_TcpClient
{
    private TcpClient _client;
    private readonly Queue<Byte[]> _sendQueue = new();

    public AvCodersTcpClient(string host, ushort port = 23) :
        base(host, port)
    {
        UpdateConnectionState(ConnectionState.Unknown);
        _client = new TcpClient();
        
        ConnectionStateWorker.Restart();
        ReceiveThreadWorker.Restart();
        SendQueueWorker.Restart();
    }

    public override void Receive()
    {
        if (!_client.Connected)
        {
            Log("Client disconnected, waiting 5 seconds");
            Thread.Sleep(5000);
        }
        else
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
                Log($"Receive - IOException:\n{e}", EventLevel.Error);
                Log(e.StackTrace ?? "No Stack Trace available", EventLevel.Error);
                Reconnect();
                UpdateConnectionState(ConnectionState.Disconnected);
            }
            catch (ObjectDisposedException e)
            {
                Log($"Receive  - ObjectDisposedException\n{e}", EventLevel.Error);
                Log(e.StackTrace ?? "No Stack Trace available", EventLevel.Error);
                Reconnect();
                UpdateConnectionState(ConnectionState.Disconnected);
            }
            catch (Exception e)
            {
                Log($"Receive  - Exception:\n{e}", EventLevel.Error);
                Log(e.StackTrace ?? "No Stack Trace available", EventLevel.Error);
                Reconnect();
                UpdateConnectionState(ConnectionState.Disconnected);
            }
            Thread.Sleep(TimeSpan.FromMilliseconds(30));
        }
    }

    public override void CheckConnectionState()
    {
        if (_client.Connected)
            UpdateConnectionState(ConnectionState.Connected);
        else
        {
            UpdateConnectionState(ConnectionState.Connecting);
            try
            {
                var connectResult = _client.BeginConnect(Host, Port, null, null);
                var success = connectResult.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1));

                if (!success)
                    UpdateConnectionState(ConnectionState.Disconnected);

                _client.EndConnect(connectResult);
            }
            catch (SocketException e)
            {
                Log($"Check Connection State  - Socket Exception:{e.Message}", EventLevel.Error);
                UpdateConnectionState(ConnectionState.Disconnected);
            }
            catch (Exception e)
            {
                Log($"Check Connection State  - New Exception:{e.Message}", EventLevel.Error);
                Log(e.GetType().ToString(), EventLevel.Error);
                Log(e.StackTrace ?? "No Stack Trace available", EventLevel.Error);
                UpdateConnectionState(ConnectionState.Disconnected);
            }
        }
            
        Thread.Sleep(5000);
    }

    public override void ProcessSendQueue()
    {
        if (!_client.Connected)
            Thread.Sleep(1000);
        else
        {
            while (_sendQueue.Count > 0)
            {
                _client.GetStream().Write(_sendQueue.Dequeue());
            }
            Thread.Sleep(1000);
        }
    }

    public override void Send(string message)
    {
        byte[] bytes = Bytes.FromString(message);
        Send(bytes);
    }

    public override void Send(byte[] bytes)
    {
        if (_client.Connected)
            _client.GetStream().Write(bytes);
        else
            _sendQueue.Enqueue(bytes);
    }

    public override void SetPort(ushort port)
    {
        Log($"Setting port to {port}");
        Port = port;
        Reconnect();
    }

    public override void SetHost(string host)
    {
        Log($"Setting host to {host}");
        Host = host;
        Reconnect();
    }

    public override void Connect()
    {
        _sendQueue.Clear();
        ConnectionStateWorker.Restart();
    }

    public override void Reconnect()
    {
        Log($"Reconnecting");
        UpdateConnectionState(ConnectionState.Disconnecting);
        _client = new TcpClient();
        UpdateConnectionState(ConnectionState.Disconnected);
        // The worker will handle reconnection
    }

    public override void Disconnect()
    {
        Log($"Disconnecting");
        ConnectionStateWorker.Stop();
        UpdateConnectionState(ConnectionState.Disconnecting);
        _client = new TcpClient();
        UpdateConnectionState(ConnectionState.Disconnected);
    }
}