using System.Net;
using System.Text;

namespace AVCoders.CommunicationClients;

public class AvCodersUdpSocket : IpComms
{
    private System.Net.Sockets.UdpClient _client;

    public AvCodersUdpSocket(string host, ushort port) : base(host, port)
    {
        _client = new System.Net.Sockets.UdpClient(Port);
        
        ConnectionStateWorker.Restart();
        ReceiveThreadWorker.Restart();
        SendQueueWorker.Restart();
    }

    public override void Receive()
    {
        while (true)
        {
            var foo = new IPEndPoint(IPAddress.Any, 21076);
            Thread.Sleep(100);
            if (_client.Available > 0)
                ResponseHandlers?.Invoke(Encoding.ASCII.GetString(_client.Receive(ref foo)));
            Thread.Sleep(1000);
        }
    }

    public override void ProcessSendQueue()
    {
        throw new NotImplementedException();
    }

    public override void CheckConnectionState()
    {
        throw new NotImplementedException();
    }

    public override void SetPort(ushort port)
    {
        Port = port;
        _client.Close();
        _client.Dispose();
        _client = new System.Net.Sockets.UdpClient(Port);
    }

    public override void SetHost(string host)
    {
        Log("The host will always be this device", EventLevel.Error);
    }

    public override void Connect()
    {
        throw new NotImplementedException();
    }

    public override void Reconnect()
    {
        throw new NotImplementedException();
    }

    public override void Disconnect()
    {
        throw new NotImplementedException();
    }

    public override void Send(byte[] bytes)
    {
        try
        {
            Log("UDP Scoket send is sending bytes.\nPort: {Port}\nBytes: {ConvertByteArrayToString(bytes)}");
            _client.Send(bytes, bytes.Length);
            Log("UDP Client send Try block complete");
        }
        catch (Exception e)
        {
            Log($"Error in UDP Socket Implementation. Error: {e.Message}");
        }
    }

    public override void Send(String message)
    {
        Log($"UDP Client send is sending a string. {message}");

        byte[] bytes = ConvertStringToByteArray(message);

        Send(bytes);
    }

    private static byte[] ConvertStringToByteArray(string input)
    {
        byte[] byteArray = new byte[input.Length];

        for (int i = 0; i < input.Length; i++)
        {
            byteArray[i] = (byte)input[i];
        }

        return byteArray;
    }

    private new void Log(string message, EventLevel level)
    {
        LogHandlers?.Invoke($"{DateTime.Now} - UDP Client for {Host}:{Port} - {message}", level);
    }

    private new void Error(string message)
    {
        LogHandlers?.Invoke($"{DateTime.Now} - UDP Client for {Host}:{Port} - {message}", EventLevel.Error);
    }
}