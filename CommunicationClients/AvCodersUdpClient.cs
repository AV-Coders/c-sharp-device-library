using System.Text;

namespace AVCoders.CommunicationClients;

public class AvCodersUdpClient : UdpClient
{
    private System.Net.Sockets.UdpClient _client;

    public AvCodersUdpClient(string host, ushort port = 0) : base(host, port)
    {
        _client = new System.Net.Sockets.UdpClient(Host, Port);
    }

    public override void SetPort(ushort port)
    {
        Port = port;
        _client.Close();
        _client.Dispose();
        _client = new System.Net.Sockets.UdpClient(Host, Port);
    }

    public override void SetHost(string host)
    {
        Host = host;
        _client.Close();
        _client.Dispose();
        _client = new System.Net.Sockets.UdpClient(Host, Port);
    }

    public override void Send(byte[] bytes)
    {
        try
        {
            Log("UDP Client send is sending bytes.\nHost: {Host}\nPort: {Port}\nBytes: {ConvertByteArrayToString(bytes)}");
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
        Log($"\nUDP Client send is sending a string.\nMessage: {message}\n");

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

    private void Log(String message, EventLevel level = EventLevel.Informational)
    {
        LogHandlers?.Invoke(message, level);
    }
}