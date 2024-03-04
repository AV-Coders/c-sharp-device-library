using System.Net;
using System.Text;

namespace AVCoders.CommunicationClients;

public class AvCodersUdpSocket : IpComms
{
    private System.Net.Sockets.UdpClient _client;
    private Thread _receiveThread;

    public AvCodersUdpSocket(string host, ushort port) : base(host, port)
    {
        _client = new System.Net.Sockets.UdpClient(Port);
        _receiveThread = new Thread(() =>
        {
            while (true)
            {
                var foo = new IPEndPoint(IPAddress.Any, 21076);
                Thread.Sleep(100);
                if (_client.Available > 0)
                    ResponseHandlers?.Invoke(Encoding.ASCII.GetString(_client.Receive(ref foo)));
                Thread.Sleep(1000);
            }
        });
        _receiveThread.Start();
    }

    public override void SetPort(ushort port)
    {
        Port = port;
        _client.Close();
        _client.Dispose();
        _client = new System.Net.Sockets.UdpClient(Port);
        _receiveThread.Start();
    }

    public override void SetHost(string host)
    {
        Log("The host will always be this device", EventLevel.Error);
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

    private void Log(String message, EventLevel level = EventLevel.Informational)
    {
        LogHandlers?.Invoke(message, level);
    }
}