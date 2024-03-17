using System.Net;
using Core_UdpClient = AVCoders.Core.UdpClient;
using UdpClient = System.Net.Sockets.UdpClient;

namespace AVCoders.CommunicationClients;

public class AvCodersUdpClient : Core_UdpClient
{
    private UdpClient? _client;
    private IPEndPoint? _ipEndPoint;
    private readonly Queue<QueuedPayload<Byte[]>> _sendQueue = new();

    public AvCodersUdpClient(string ipAddress, ushort port = 0) : 
        base(ipAddress, port)
    {
        _client = CreateClient();
        
        if (IPAddress.TryParse(Host, out var remoteIpAddress))
            _ipEndPoint = new IPEndPoint(remoteIpAddress, Port);
        
        // This works around a race condition coming from base being called first.
        ReceiveThreadWorker.Restart();
    }

    private UdpClient? CreateClient()
    {
        try
        {
            UpdateConnectionState(ConnectionState.Connecting);
            var client = new UdpClient(Host, Port);
            if (IPAddress.TryParse(Host, out var remoteIpAddress))
                _ipEndPoint = new IPEndPoint(remoteIpAddress, Port);
            ReceiveThreadWorker.Restart();
            ConnectionStateWorker.Restart();
            UpdateConnectionState(ConnectionState.Connected);
            return client;
        }
        catch( Exception e)
        {
           Log($"Exception while connecting: {e.Message}\r\n{e.StackTrace}", EventLevel.Error);
        }
        UpdateConnectionState(ConnectionState.Disconnected);
        return null;
    }

    protected override void Receive()
    {
        if (_ipEndPoint == null)
        {
            ReceiveThreadWorker.Stop();
            return;
        }

        if (_client is not { Available: > 0 })
        {
            Thread.Sleep(1100);
            return;
        }
        try
        {
            ResponseHandlers?.Invoke(ConvertByteArrayToString(_client.Receive(ref _ipEndPoint)));
        }
        catch (Exception e)
        {
            Log($"Receive - Error: {e.Message}");
        }
    }

    protected override void ProcessSendQueue()
    {
        if (_client == null)
        {
            Log("Messages in send queue will not be sent while client is not connected");
            Thread.Sleep(500);
        }
        else
        {
            while (_sendQueue.Count > 0)
            {
                var item = _sendQueue.Dequeue();
                if (Math.Abs((DateTime.Now - item.Timestamp).TotalSeconds) < QueueTimeout)
                    _client.Send(item.Payload);
            }
            Thread.Sleep(1100);
        }
    }

    protected override void CheckConnectionState()
    {
        if (ConnectionState != ConnectionState.Connected || ConnectionState != ConnectionState.Connecting)
        {
            CreateClient();
        }
    }

    public override void SetPort(ushort port)
    {
        Port = port;
        Reconnect();
    }

    public override void SetHost(string host)
    {
        Host = host;
        Reconnect();
    }

    public override void Connect()
    {
        _sendQueue.Clear();
        Reconnect();
    }

    public override void Reconnect()
    {
        Log($"Reconnecting");
        UpdateConnectionState(ConnectionState.Disconnecting);
        _client?.Close();
        CreateClient();
        UpdateConnectionState(ConnectionState.Disconnected);
    }

    public override void Disconnect()
    {
        Log($"Disconnecting");
        UpdateConnectionState(ConnectionState.Disconnecting);
        _client = null;
        _ipEndPoint = null;
        ReceiveThreadWorker.Stop();
        ConnectionStateWorker.Stop();
        UpdateConnectionState(ConnectionState.Disconnected);
    }

    public override void Send(byte[] bytes)
    {
        try
        {
            if (_client == null)
            {
                _sendQueue.Enqueue(new QueuedPayload<byte[]>(DateTime.Now, bytes));
                return;
            }
            _client.Send(bytes, bytes.Length);
        }
        catch (Exception e)
        {
            Log($"Send - Error: {e.Message}\r\n {e.StackTrace}");
        }
    }

    public override void Send(String message) => Send(ConvertStringToByteArray(message));
}