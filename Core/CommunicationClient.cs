using System.Text;

namespace AVCoders.Core;

public abstract class CommunicationClient
{
    public ResponseHandler? ResponseHandlers;
    public ResponseByteHandler? ResponseByteHandlers;
    public ConnectionStateHandler? ConnectionStateHandlers;
    public LogHandler? LogHandlers;
    protected ConnectionState ConnectionState = ConnectionState.Unknown;

    public abstract void Send(string message);
    public abstract void Send(byte[] bytes);
    protected void Log(string message, EventLevel level = EventLevel.Informational) => LogHandlers?.Invoke(message, level);
    public ConnectionState GetConnectionState() => ConnectionState;
    
    protected void UpdateConnectionState(ConnectionState connectionState)
    {
        if (ConnectionState == connectionState)
            return;
        ConnectionState = connectionState;
        ConnectionStateHandlers?.Invoke(connectionState);
    }
}

public abstract class SerialClient : CommunicationClient
{
    public abstract void ConfigurePort(SerialSpec serialSpec);

    public abstract void Send(char[] chars);
}

public abstract class IpComms : CommunicationClient
{
    protected string Host;
    protected ushort Port;
    protected int QueueTimeout = 5;
    
    protected readonly ThreadWorker ReceiveThreadWorker;
    protected readonly ThreadWorker ConnectionStateWorker;
    protected readonly ThreadWorker SendQueueWorker;

    protected IpComms(string host, ushort port)
    {
        Host = host;
        Port = port;
        
        ReceiveThreadWorker = new ThreadWorker(Receive, TimeSpan.Zero);
        SendQueueWorker = new ThreadWorker(ProcessSendQueue, TimeSpan.Zero);
        ConnectionStateWorker = new ThreadWorker(CheckConnectionState, TimeSpan.Zero);
    }

    ~IpComms()
    {
        ReceiveThreadWorker.Stop();
        ConnectionStateWorker.Stop();
        SendQueueWorker.Stop();
    }

    protected abstract void Receive();

    protected abstract void ProcessSendQueue();

    protected abstract void CheckConnectionState();

    public void SetQueueTimeout(int seconds) => QueueTimeout = seconds;

    public string GetHost() => Host;
    
    public abstract void SetHost(string host);

    public ushort GetPort() => Port;

    public abstract void SetPort(ushort port);

    public abstract void Connect();

    public abstract void Reconnect();

    public abstract void Disconnect();

    protected static byte[] ConvertStringToByteArray(string input)
    {
        byte[] byteArray = new byte[input.Length];

        for (int i = 0; i < input.Length; i++)
        {
            byteArray[i] = (byte)input[i];
        }

        return byteArray;
    }

    protected static string ConvertByteArrayToString(byte[] byteArray)
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

public abstract class TcpClient : IpComms
{
    protected TcpClient(string host, ushort port) : base(host, port)
    {
    }
}

public abstract class UdpClient : IpComms
{

    protected UdpClient(string host, ushort port) : base(host, port)
    {
    }
}