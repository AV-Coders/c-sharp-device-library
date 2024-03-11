using System.Net;
using Core_UdpClient = AVCoders.Core.UdpClient;
using UdpClient = System.Net.Sockets.UdpClient;

namespace AVCoders.CommunicationClients;

public class AvCodersUdpClient : Core_UdpClient
{
    private UdpClient _client;
    private IPEndPoint? _ipEndPoint;
    private readonly Queue<Byte[]> _sendQueue = new();

    public AvCodersUdpClient(string ipAddress, ushort port = 0) : 
        base(ipAddress, port)
    {
        _client = new UdpClient(Host, Port);
        
        if (IPAddress.TryParse(Host, out var remoteIpAddress))
            _ipEndPoint = new IPEndPoint(remoteIpAddress, Port);
        
        // This works around a race condition coming from base being called first.
        ReceiveThreadWorker.Restart();
    }

    protected override void Receive()
    {
        if (_ipEndPoint == null)
        {
            ReceiveThreadWorker.Stop();
            return;
        }

        if (_client.Available <= 0)
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

    protected override void ProcessSendQueue() => SendQueueWorker.Stop();

    protected override void CheckConnectionState() => ConnectionStateWorker.Stop();

    public override void SetPort(ushort port)
    {
        Port = port;
        if (IPAddress.TryParse(Host, out var remoteIpAddress))
            _ipEndPoint = new IPEndPoint(remoteIpAddress, Port);
        Reconnect();
    }

    public override void SetHost(string host)
    {
        Host = host;
        if (IPAddress.TryParse(Host, out var remoteIpAddress))
            _ipEndPoint = new IPEndPoint(remoteIpAddress, Port);
        Reconnect();
    }

    public override void Connect()
    {
        _sendQueue.Clear();
        Reconnect();
        ConnectionStateWorker.Restart();
    }

    public override void Reconnect()
    {
        Log($"Reconnecting");
        UpdateConnectionState(ConnectionState.Disconnecting);
        _client.Close();
        _client = new UdpClient(Host, Port);
        if (IPAddress.TryParse(Host, out var remoteIpAddress))
            _ipEndPoint = new IPEndPoint(remoteIpAddress, Port);
        ReceiveThreadWorker.Restart();
        UpdateConnectionState(ConnectionState.Disconnected);
    }

    public override void Disconnect()
    {
        Log($"Disconnecting");
        UpdateConnectionState(ConnectionState.Disconnecting);
        _ipEndPoint = null;
        ReceiveThreadWorker.Stop();
        ConnectionStateWorker.Stop();
        _client.Close();
        _client = new UdpClient();
        UpdateConnectionState(ConnectionState.Disconnected);
    }

    public override void Send(byte[] bytes)
    {
        try
        {
            _client.Send(bytes, bytes.Length);
        }
        catch (Exception e)
        {
            Log($"Send - Error: {e.Message}\r\n {e.StackTrace}");
        }
    }

    public override void Send(String message) => Send(ConvertStringToByteArray(message));
}