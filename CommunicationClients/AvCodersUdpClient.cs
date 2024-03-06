using System.Net;
using System.Text;
using Core_UdpClient = AVCoders.Core.UdpClient;
using UdpClient = System.Net.Sockets.UdpClient;

namespace AVCoders.CommunicationClients;

public class AvCodersUdpClient : Core_UdpClient
{
    private UdpClient _client;
    private IPEndPoint? _ipEndPoint;
    private readonly Queue<Byte[]> _sendQueue = new();

    public AvCodersUdpClient(string host, ushort port = 0) : 
        base(host, port)
    {
        _client = new UdpClient(Host, Port);
        
        if (IPAddress.TryParse(Host, out var remoteIpAddress))
            _ipEndPoint = new IPEndPoint(remoteIpAddress, Port);
        
        // This works around a race condition coming from base being called first.
        ReceiveThreadWorker.Restart();
    }

    public override void Receive()
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

    public override void ProcessSendQueue() =>SendQueueWorker.Stop();

    public override void CheckConnectionState() => ConnectionStateWorker.Stop();

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
        _client = new UdpClient(Host, Port);
        ReceiveThreadWorker.Restart();
    }

    public override void Reconnect()
    {
        Log($"Reconnecting");
        UpdateConnectionState(ConnectionState.Disconnecting);
        _client = new UdpClient(Host, Port);
        ReceiveThreadWorker.Restart();
        UpdateConnectionState(ConnectionState.Disconnected);
    }

    public override void Disconnect()
    {
        Log($"Disconnecting");
        ConnectionStateWorker.Stop();
        UpdateConnectionState(ConnectionState.Disconnecting);
        _client.Close();
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
            Log($"Send - Error: {e.Message}");
        }
    }

    public override void Send(String message) => Send(ConvertStringToByteArray(message));

    private static byte[] ConvertStringToByteArray(string input)
    {
        byte[] byteArray = new byte[input.Length];

        for (int i = 0; i < input.Length; i++)
        {
            byteArray[i] = (byte)input[i];
        }

        return byteArray;
    }

    private static string ConvertByteArrayToString(byte[] byteArray)
    {
        StringBuilder sb = new StringBuilder(byteArray.Length * 2);

        foreach (byte b in byteArray)
        {
            sb.AppendFormat("\\x{0:X2} ", b);
        }

        // Remove the last space character if needed
        if (sb.Length > 0)
            sb.Length--;

        return sb.ToString();
    }
}