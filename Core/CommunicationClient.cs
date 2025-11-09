using System.Text;
using Serilog;
using Serilog.Context;

namespace AVCoders.Core;

public abstract class CommunicationClient(string name, string host, ushort port, CommandStringFormat commandStringFormat) : LogBase(name)
{
    private string _host = host;
    private ushort _port = port;
    public StringHandler? RequestHandlers;
    public ByteHandler? RequestByteHandlers;
    public StringHandler? ResponseHandlers;
    public ByteHandler? ResponseByteHandlers;
    public ConnectionStateHandler? ConnectionStateHandlers;
    public readonly CommandStringFormat CommandStringFormat = commandStringFormat;

    public string Host
    {
        get => _host;
        protected set => _host = value;
    }

    public ushort Port
    {
        get => _port;
        protected set => _port = value;
    }

    private ConnectionState _connectionState = ConnectionState.Unknown;

    public ConnectionState ConnectionState
    {
        get => _connectionState;
        protected set
        {
            if(_connectionState == value)
                return;
            _connectionState = value;
            try
            {
                ConnectionStateHandlers?.Invoke(value);
            }
            catch (Exception e)
            {
                LogException(e, "A ConnectionState handler threw an exception");
            }
        }
    }

    /// <summary>
    /// A no-op CommunicationClient used to indicate that there is no real communication backend.
    /// </summary>
    public static CommunicationClient None { get; } = new NoneCommunicationClient();

    /// <summary>
    /// Internal no-op implementation. All operations are ignored.
    /// </summary>
    private sealed class NoneCommunicationClient() : CommunicationClient("None", "None", 0, CommandStringFormat.Ascii)
    {
        public override void Send(string message)
        {
            // intentionally no-op
        }

        public override void Send(byte[] bytes)
        {
            // intentionally no-op
        }
    }

    public abstract void Send(string message);
    public abstract void Send(byte[] bytes);

    protected void InvokeRequestHandlers(string request)
    {
        try
        {
            RequestHandlers?.Invoke(request);
        }
        catch (Exception e)
        {
            LogException(e, "A Request string handler threw an exception");
        }
    }

    protected void InvokeRequestHandlers(byte[] request)
    {
        try
        {
            RequestByteHandlers?.Invoke(request);
        }
        catch (Exception e)
        {
            LogException(e, "A Request byte handler threw an exception");
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
            using (PushProperties("InvokeResponseHandlers"))
            {
                LogException(e, "A Response handler threw an exception");
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
            LogException(e, $"A Response handler threw an exception when given response:\n\t {response}");
        }
    }
}

public abstract class SerialClient(string name, string host, ushort port, CommandStringFormat commandStringFormat)
    : CommunicationClient(name, host, port, commandStringFormat)
{
    public abstract void ConfigurePort(SerialSpec serialSpec);

    public abstract void Send(char[] chars);
}

public abstract class RestComms(string host, ushort port, string name) 
    : CommunicationClient(name, host, port, CommandStringFormat.Ascii)
{
    public HttpResponseHandler? HttpResponseHandlers;

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
    protected int QueueTimeout = 5;
    
    protected readonly ThreadWorker ReceiveThreadWorker;
    protected readonly ThreadWorker ConnectionStateWorker;
    protected readonly ThreadWorker SendQueueWorker;

    protected IpComms(string host, ushort port, string name, CommandStringFormat commandStringFormat)
        : base(name, host, port, commandStringFormat)
    {
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

public abstract class IMulticastClient : IpComms
{
    protected IMulticastClient(string host, ushort port, string name, CommandStringFormat commandStringFormat)
        : base(host, port, name, commandStringFormat)
    {
    }
}

public abstract class MqttClient(string host, ushort port, string name)
    : CommunicationClient(name, host, port, CommandStringFormat.Ascii)
{
    public abstract void Send(string topic, string payload);
    public abstract void SubscribeToTopic(string topic, Action<string> handler);
}

public abstract class SshClient(string host, ushort port, string name, CommandStringFormat commandStringFormat)
    : IpComms(host, port, name, commandStringFormat);

public abstract class TcpClient(string host, ushort port, string name, CommandStringFormat commandStringFormat)
    : IpComms(host, port, name, commandStringFormat);

public abstract class UdpClient(string host, ushort port, string name, CommandStringFormat commandStringFormat)
    : IpComms(host, port, name, commandStringFormat);

public interface IWakeOnLan
{
    public void Wake(string mac);
}