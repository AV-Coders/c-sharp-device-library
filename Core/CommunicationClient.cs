namespace AVCoders.Core;

public abstract class CommunicationClient
{
    public ResponseHandler? ResponseHandlers;
    public ConnectionStateHandler? ConnectionStateHandlers;
    public LogHandler? LogHandlers;
    protected ConnectionState ConnectionState = ConnectionState.Unknown;

    public abstract void Send(string message);
    public abstract void Send(byte[] bytes);
    private void Log(string message) => LogHandlers?.Invoke($"CommunicationClient - {message}");
    public ConnectionState GetConnectionState() => ConnectionState;
}

public abstract class SerialClient : CommunicationClient
{
    public abstract void ConfigurePort(SerialSpec serialSpec);
}

public abstract class IpComms : CommunicationClient
{
    protected string Host;
    protected ushort Port;

    protected IpComms(string host, ushort port)
    {
        Host = host;
        Port = port;
    }

    public abstract void SetPort(ushort port);
    public abstract void SetHost(string host);
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