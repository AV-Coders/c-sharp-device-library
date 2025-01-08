﻿using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AVCoders.Core;

public abstract class CommunicationClient
{
    public StringHandler? Requesthandlers;
    public ByteHandler? RequestBytehandlers;
    public StringHandler? ResponseHandlers;
    public ByteHandler? ResponseByteHandlers;
    public ConnectionStateHandler? ConnectionStateHandlers;
    public LogHandler? LogHandlers;
    protected ConnectionState ConnectionState = ConnectionState.Unknown;
    public readonly string Name;

    protected CommunicationClient(string name)
    {
        Name = name;
    }

    public abstract void Send(string message);
    public abstract void Send(byte[] bytes);
    protected void Log(string message, EventLevel level = EventLevel.Informational) => LogHandlers?.Invoke($"{Name} - {message}", level);
    protected void Error(string message, EventLevel level = EventLevel.Error) => LogHandlers?.Invoke($"{Name} - {message}", level);
    public ConnectionState GetConnectionState() => ConnectionState;
    
    protected void UpdateConnectionState(ConnectionState connectionState)
    {
        if (ConnectionState == connectionState)
            return;
        ConnectionState = connectionState;
        ConnectionStateHandlers?.Invoke(connectionState);
    }

    protected void InvokeRequestHandlers(string request)
    {
        try
        {
            Requesthandlers?.Invoke(request);
        }
        catch (Exception e)
        {
            Error("A Request string handler threw an exception");
            Error(e.Message);
            Error(e.StackTrace?? "No Stack trace available");
            Error(e.InnerException?.Message ?? "No inner exception");
            Error(e.InnerException?.StackTrace?? String.Empty);
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
            Error("A Request byte handler threw an exception");
            Error(e.Message);
            Error(e.StackTrace?? "No Stack trace available");
            Error(e.InnerException?.Message ?? "No inner exception");
            Error(e.InnerException?.StackTrace?? String.Empty);
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
            Error("A Response handler threw an exception");
            Error(e.Message);
            Error(e.StackTrace?? "No Stack trace available");
            Error(e.InnerException?.Message ?? "No inner exception");
            Error(e.InnerException?.StackTrace?? String.Empty);
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
            Error("A Response handler threw an exception");
            Error(e.Message);
            Error(e.StackTrace?? "No Stack trace available");
            Error(e.InnerException?.Message ?? "No inner exception");
            Error(e.InnerException?.StackTrace?? String.Empty);
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

    // ~RestComms()
    // {
    // }
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