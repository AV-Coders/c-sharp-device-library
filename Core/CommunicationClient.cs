using System.Text;
using Serilog;
using Serilog.Context;

namespace AVCoders.Core;

public abstract class CommunicationClient(string name) : LogBase(name)
{
    public StringHandler? Requesthandlers;
    public ByteHandler? RequestBytehandlers;
    public StringHandler? ResponseHandlers;
    public ByteHandler? ResponseByteHandlers;
    public ConnectionStateHandler? ConnectionStateHandlers;

    private ConnectionState _connectionState = ConnectionState.Unknown;

    public ConnectionState ConnectionState
    {
        get => _connectionState;
        protected set
        {
            if(_connectionState == value)
                return;
            _connectionState = value;
            ConnectionStateHandlers?.Invoke(value);
        }
    }

    public abstract void Send(string message);
    public abstract void Send(byte[] bytes);
    public ConnectionState GetConnectionState() => ConnectionState;

    protected void InvokeRequestHandlers(string request)
    {
        try
        {
            Requesthandlers?.Invoke(request);
        }
        catch (Exception e)
        {
            Log.Error("A Request string handler threw an exception");
            LogException(e);
        }
    }

    protected void InvokeRequestHandlers(byte[] request)
    {
        try
        {
            RequestBytehandlers?.Invoke(request);
        }
        catch (Exception e)
        {
            Log.Error("A Request byte handler threw an exception");
            LogException(e);
        }
    }

    protected void InvokeResponseHandlers(string response, byte[] responseBytes)
    {
        try
        {
            ResponseHandlers?.Invoke(response);
            ResponseByteHandlers?.Invoke(responseBytes);
        }
        catch (Exception e)
        {
            using (LogContext.PushProperty(MethodProperty, "InvokeResponseHandlers"))
            {
                Log.Error("A Response handler threw an exception");
                LogException(e);
            }
        }
    }

    protected void InvokeResponseHandlers(string response)
    {
        try
        {
            ResponseHandlers?.Invoke(response);
        }
        catch (Exception e)
        {
            Log.Error("A Response handler threw an exception when given response:\n\t {Response}", response);
            LogException(e);
        }
    }
}

public abstract class SerialClient : CommunicationClient
{
    protected SerialClient(string name) : base(name)
    { }

    public abstract void ConfigurePort(SerialSpec serialSpec);

    public abstract void Send(char[] chars);
}

public abstract class RestComms : CommunicationClient
{
    protected string Host;
    protected ushort Port;
    public HttpResponseHandler? HttpResponseHandlers;

    protected RestComms(string host, ushort port, string name) : base(name)
    {
        Host = host;
        Port = port;
    }

    public abstract void AddDefaultHeader(string key, string value);

    public abstract void RemoveDefaultHeader(string key);

    public abstract Task Post(string payload, string contentType);
    public abstract Task Post(Uri? endpoint, string payload, string contentType);
    public abstract Task Put(string payload, string contentType);
    public abstract Task Put(Uri? endpoint, string content, string contentType);

    public abstract Task Get();

    public abstract Task Get(Uri? endpoint);
}

public abstract class IpComms : CommunicationClient
{
    protected string Host;
    protected ushort Port;
    protected int QueueTimeout = 5;
    
    protected readonly ThreadWorker ReceiveThreadWorker;
    protected readonly ThreadWorker ConnectionStateWorker;
    protected readonly ThreadWorker SendQueueWorker;

    protected IpComms(string host, ushort port, string name) : base(name)
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

    protected abstract Task Receive(CancellationToken token);

    protected abstract Task ProcessSendQueue(CancellationToken token);

    protected abstract Task CheckConnectionState(CancellationToken token);

    public void SetQueueTimeout(int seconds) => QueueTimeout = seconds;

    public string GetHost() => Host;
    
    public abstract void SetHost(string host);

    public ushort GetPort() => Port;

    public abstract void SetPort(ushort port);

    public abstract void Connect();

    public abstract void Reconnect();

    public abstract void Disconnect();

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

public abstract class SshClient : IpComms
{
    protected SshClient(string host, ushort port, string name) : base(host, port, name)
    {
    }
}

public abstract class TcpClient : IpComms
{
    protected TcpClient(string host, ushort port, string name) : base(host, port, name)
    {
    }
}

public abstract class UdpClient : IpComms
{

    protected UdpClient(string host, ushort port, string name) : base(host, port, name)
    {
    }
}

public interface IWakeOnLan
{
    public void Wake(string mac);
} 