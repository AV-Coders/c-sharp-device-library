using System.Net.Sockets;
using System.Text;

namespace AVCoders.Core;

public abstract class CommunicationClient(string name, string host, ushort port, CommandStringFormat commandStringFormat) : LogBase(name)
{
    public const string ConnectionIssueKey = "connection";
    private const string RequestedDisconnect = "Disconnected on request";

    private protected readonly TimeProvider TimeProvider = TimeProvider.System;
    private string _host = host;
    private ushort _port = port;
    private readonly object _connectionIssueLock = new();
    private (long Timestamp, DateTimeOffset At)? _connectionFailingSince;
    public StringHandler? RequestHandlers;
    public ByteHandler? RequestByteHandlers;
    public StringHandler? ResponseHandlers;
    public ByteHandler? ResponseByteHandlers;
    public ConnectionStateHandler? ConnectionStateHandlers;
    public ConnectionReasonHandler? ConnectionReasonHandlers;
    public readonly CommandStringFormat CommandStringFormat = commandStringFormat;

    internal CommunicationClient(string name, string host, ushort port, CommandStringFormat commandStringFormat, TimeProvider timeProvider)
        : this(name, host, port, commandStringFormat)
    {
        TimeProvider = timeProvider;
    }

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
    private ConnectionState _desiredConnectionState = ConnectionState.Unknown;

    public ConnectionState DesiredConnectionState
    {
        get => _desiredConnectionState;
        protected set
        {
            _desiredConnectionState = value;
            if (value == ConnectionState.Disconnected || ConnectionReason?.Category == ConnectionReasonCategory.Requested)
                ResetConnectionReason();
        }
    }

    public ConnectionReason? ConnectionReason { get; private set; }

    public bool ReconnectingForSend { get; private protected set; }

    public ConnectionState ConnectionState
    {
        get => _connectionState;
        protected set
        {
            if(_connectionState == value)
                return;
            _connectionState = value;
            if (value == ConnectionState.Connected)
            {
                ReconnectingForSend = false;
                ClearConnectionFailures();
            }
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
    /// How long connection failures may persist before <see cref="ReportConnectionFailure"/>
    /// escalates them to an ongoing issue.
    /// </summary>
    public TimeSpan ConnectionIssueThreshold { get; set; } = TimeSpan.FromMinutes(2);

    protected void ReportConnectionFailure(Exception e)
    {
        var (category, description) = ExplainConnectionError(e);
        ReportConnectionFailure(category, description, e);
    }

    protected void ReportConnectionFailure(string reason) => ReportConnectionFailure(ConnectionReasonCategory.Other, reason);

    protected void ReportConnectionFailure(ConnectionReasonCategory category, string reason, Exception? exception = null)
    {
        (long Timestamp, DateTimeOffset At) failingSince;
        bool escalate;
        lock (_connectionIssueLock)
        {
            if (DesiredConnectionState == ConnectionState.Disconnected)
                return;
            _connectionFailingSince ??= (TimeProvider.GetTimestamp(), TimeProvider.GetUtcNow());
            failingSince = _connectionFailingSince.Value;
            escalate = TimeProvider.GetElapsedTime(failingSince.Timestamp) >= ConnectionIssueThreshold;
        }
        SetConnectionReason(category, reason, exception);
        RaiseMomentaryIssue(reason, key: ConnectionIssueKey);
        if (escalate)
            RaiseOngoingIssue(ConnectionIssueKey,
                $"Unable to connect since {failingSince.At.ToLocalTime():HH:mm:ss}. {reason}",
                IssueSeverity.Critical);
        bool cleared;
        lock (_connectionIssueLock)
            cleared = _connectionFailingSince != failingSince;
        if (cleared)
            ResolveIssue(ConnectionIssueKey);
    }

    protected void ClearConnectionFailures()
    {
        ResetConnectionReason();
        lock (_connectionIssueLock)
        {
            if (_connectionFailingSince == null)
                return;
            _connectionFailingSince = null;
        }
        ResolveIssue(ConnectionIssueKey);
    }

    protected void SetConnectionReason(ConnectionReasonCategory category, string description, Exception? exception = null)
    {
        if (DesiredConnectionState == ConnectionState.Disconnected)
            return;
        ReplaceConnectionReason(NewConnectionReason(category, description, exception));
    }

    protected void MirrorConnectionReason(ConnectionReason? reason) => ReplaceConnectionReason(reason);

    private void ResetConnectionReason() =>
        ReplaceConnectionReason(DesiredConnectionState == ConnectionState.Disconnected
            ? NewConnectionReason(ConnectionReasonCategory.Requested, RequestedDisconnect, null)
            : null);

    private ConnectionReason NewConnectionReason(ConnectionReasonCategory category, string description, Exception? exception) =>
        new(category, description, exception == null ? null : RawText(exception),
            ConnectionReason is { } current && current.Category == category ? current.Since : TimeProvider.GetUtcNow());

    private static string RawText(Exception e) => e.InnerException == null
        ? $"{e.GetType().FullName}: {e.Message}"
        : $"{e.GetType().FullName}: {e.Message} ---> {RawText(e.InnerException)}";

    private void ReplaceConnectionReason(ConnectionReason? reason)
    {
        lock (_connectionIssueLock)
        {
            if (Equals(ConnectionReason, reason))
                return;
            ConnectionReason = reason;
        }
        try
        {
            ConnectionReasonHandlers?.Invoke(reason);
        }
        catch (Exception e)
        {
            LogException(e, "A ConnectionReason handler threw an exception");
        }
    }

    protected string DescribeConnectionError(Exception e) => ExplainConnectionError(e).Description;

    protected virtual (ConnectionReasonCategory Category, string Description) ExplainConnectionError(Exception e) => e switch
    {
        SocketException s => s.SocketErrorCode switch
        {
            SocketError.TimedOut => (ConnectionReasonCategory.TimedOut, $"The connection to {Host}:{Port} timed out"),
            SocketError.HostNotFound => (ConnectionReasonCategory.HostNotFound, $"The host {Host} was not found"),
            SocketError.ConnectionRefused => (ConnectionReasonCategory.Refused, $"{Host}:{Port} refused the connection"),
            SocketError.HostUnreachable => (ConnectionReasonCategory.Unreachable, $"The host {Host} is unreachable"),
            SocketError.NetworkUnreachable => (ConnectionReasonCategory.Unreachable, $"The network is unreachable trying to reach {Host}"),
            SocketError.ConnectionReset => (ConnectionReasonCategory.ClosedByDevice, $"The connection to {Host}:{Port} was reset"),
            _ => (ConnectionReasonCategory.Other, $"The connection to {Host}:{Port} failed: {s.Message}")
        },
        HttpRequestException { InnerException: SocketException inner } => ExplainConnectionError(inner),
        HttpRequestException h => (ConnectionReasonCategory.Other, $"The request to {Host}:{Port} failed: {h.Message}"),
        TimeoutException => (ConnectionReasonCategory.TimedOut, $"The connection to {Host}:{Port} timed out"),
        OperationCanceledException => (ConnectionReasonCategory.TimedOut, $"The connection attempt to {Host}:{Port} timed out"),
        _ when e.InnerException is SocketException inner => ExplainConnectionError(inner),
        _ => (ConnectionReasonCategory.Other, $"The connection to {Host}:{Port} failed: {e.Message}")
    };

    /// <summary>
    /// A no-op CommunicationClient used to indicate that there is no real communication backend.
    /// </summary>
    public static CommunicationClient None { get; } = new NoneCommunicationClient();

    /// <summary>
    /// Internal no-op implementation. All operations are ignored.
    /// </summary>
    private sealed class NoneCommunicationClient : CommunicationClient
    {
        public NoneCommunicationClient() : base("None", "None", 0, CommandStringFormat.Ascii)
        {
            LogBaseRegistry.Deregister(this);
        }

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
        using (PushProperties("InvokeRequestHandlers"))
        {
            LogVerbose("Sending: {Request}", request);
            try
            {
                RequestHandlers?.Invoke(request);
            }
            catch (Exception e)
            {
                LogException(e, "A Request string handler threw an exception");
            }
        }
    }

    protected void InvokeRequestHandlers(byte[] request)
    {
        using (PushProperties("InvokeRequestHandlers"))
        {
            LogVerbose("Sending bytes: {Request}", BitConverter.ToString(request));
            try
            {
                RequestByteHandlers?.Invoke(request);
            }
            catch (Exception e)
            {
                LogException(e, "A Request byte handler threw an exception");
            }
        }
    }

    protected void InvokeResponseHandlers(string response, byte[] responseBytes)
    {
        using (PushProperties("InvokeResponseHandlers"))
        {
            LogVerbose("Received: {Response}", response);
            try
            {
                ResponseHandlers?.Invoke(response);
                ResponseByteHandlers?.Invoke(responseBytes);
            }
            catch (Exception e)
            {
                LogException(e, "A Response handler threw an exception");
            }
        }
    }

    protected void InvokeResponseHandlers(string response)
    {
        using (PushProperties("InvokeResponseHandlers"))
        {
            LogVerbose("Received: {Response}", response);
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
}

public abstract class ModbusClient : CommunicationClient
{
    protected ModbusClient(String name, string host, ushort port, CommandStringFormat commandStringFormat)
        : base(name, host, port, commandStringFormat)
    {
    }

    internal ModbusClient(String name, string host, ushort port, CommandStringFormat commandStringFormat, TimeProvider timeProvider)
        : base(name, host, port, commandStringFormat, timeProvider)
    {
    }

    public abstract Task<bool[]> ReadCoils(byte deviceId, ushort startCoil, ushort count, CancellationToken token = default);
    public abstract Task<ushort[]> ReadHoldingRegisters(byte deviceId, ushort startRegister, ushort count, CancellationToken token = default);
    public abstract Task WriteCoil(byte deviceId, ushort coil, bool value, CancellationToken token = default);
    public abstract Task WriteCoils(byte deviceId, ushort startCoil, bool[] values, CancellationToken token = default);
    public abstract Task WriteRegister(byte deviceId, ushort register, ushort value, CancellationToken token = default);
    public abstract Task WriteRegisters(byte deviceId, ushort startRegister, ushort[] values, CancellationToken token = default);
}

public class ModbusException(byte functionCode, byte exceptionCode, string message) : Exception(message)
{
    public byte FunctionCode { get; } = functionCode;
    public byte ExceptionCode { get; } = exceptionCode;
}

public abstract class ModbusRtuClient : ModbusClient
{
    protected ModbusRtuClient(String name, string host, ushort port)
        : base(name, host, port, CommandStringFormat.Hex)
    {
    }

    internal ModbusRtuClient(String name, string host, ushort port, TimeProvider timeProvider)
        : base(name, host, port, CommandStringFormat.Hex, timeProvider)
    {
    }
}

public abstract class ModbusAsciiClient(String name, string host, ushort port)
    : ModbusClient(name, host, port, CommandStringFormat.Hex)
{

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
    protected const int ReconnectingSendQueueTimeout = 15;
    protected int MaxQueueSize = 100;

    protected readonly ThreadWorker ReceiveThreadWorker;
    protected readonly ThreadWorker ConnectionStateWorker;
    protected readonly ThreadWorker SendQueueWorker;

    protected readonly object ConnectionRequestLock = new();
    public BoolHandler? ReconnectOnSendHandlers;

    public Actor? DisconnectedBy { get; private set; }

    public bool ReconnectOnSend { get; private set; }

    protected IpComms(string host, ushort port, string name, CommandStringFormat commandStringFormat)
        : this(host, port, name, commandStringFormat, TimeProvider.System)
    {
    }

    internal IpComms(string host, ushort port, string name, CommandStringFormat commandStringFormat, TimeProvider timeProvider)
        : base(name, host, port, commandStringFormat, timeProvider)
    {
        ReceiveThreadWorker = new ThreadWorker(Receive, TimeSpan.Zero, false, timeProvider);
        SendQueueWorker = new ThreadWorker(ProcessSendQueue, TimeSpan.Zero, false, timeProvider);
        ConnectionStateWorker = new ThreadWorker(CheckConnectionState, TimeSpan.Zero, false, timeProvider);
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

    protected int QueueTimeoutFor(bool waitsForTheReconnect) =>
        waitsForTheReconnect ? Math.Max(QueueTimeout, ReconnectingSendQueueTimeout) : QueueTimeout;

    protected bool WaitsForTheReconnect() => ReconnectingForSend && Actor.Current != null;

    public abstract void Connect();

    public abstract void Reconnect();

    public abstract void Disconnect();

    public void SetReconnectOnSend(bool reconnectOnSend)
    {
        lock (ConnectionRequestLock)
            SetDisconnectedBy(DisconnectedBy, reconnectOnSend);
    }

    protected void RecordRequestedDisconnect()
    {
        ReconnectingForSend = false;
        if (DesiredConnectionState == ConnectionState.Disconnected)
            return;
        var person = Actor.Current;
        SetDisconnectedBy(person, person != null);
    }

    protected void RecordRequestedConnect() => SetDisconnectedBy(null, false);

    private void SetDisconnectedBy(Actor? disconnectedBy, bool reconnectOnSend)
    {
        var reconnectOnSendChanged = ReconnectOnSend != reconnectOnSend;
        if (!reconnectOnSendChanged && Equals(DisconnectedBy, disconnectedBy))
            return;
        DisconnectedBy = disconnectedBy;
        ReconnectOnSend = reconnectOnSend;
        using (PushProperties("SetDisconnectedBy"))
        {
            if (reconnectOnSendChanged)
            {
                var setting = reconnectOnSend ? "on" : "off";
                LogInformation("Reconnect on the next send a person makes is {ReconnectOnSend}", setting);
                AddEvent(EventType.Connection, $"Reconnect on the next send a person makes: {setting}");
            }
            try
            {
                ReconnectOnSendHandlers?.Invoke(reconnectOnSend);
            }
            catch (Exception e)
            {
                LogException(e, "A ReconnectOnSend handler threw an exception");
            }
        }
    }

    protected bool ReconnectForSend(Action send)
    {
        if (Actor.Current is not { } sender)
            return false;
        lock (ConnectionRequestLock)
        {
            if (DesiredConnectionState == ConnectionState.Disconnected)
            {
                if (!ReconnectOnSend)
                    return false;
                using (PushProperties("ReconnectForSend"))
                    LogInformation("Reconnecting for a send by {Sender}, the client was disconnected on request by {DisconnectedBy}",
                        sender.ToString(), DisconnectedBy?.ToString());
                ReconnectingForSend = true;
                Connect();
            }
            send();
            return true;
        }
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

public abstract class IMulticastClient : IpComms
{
    protected IMulticastClient(string host, ushort port, string name, CommandStringFormat commandStringFormat)
        : this(host, port, name, commandStringFormat, TimeProvider.System)
    {
    }

    internal IMulticastClient(string host, ushort port, string name, CommandStringFormat commandStringFormat, TimeProvider timeProvider)
        : base(host, port, name, commandStringFormat, timeProvider)
    {
    }
}

public abstract class MqttClient : CommunicationClient
{
    protected MqttClient(string host, ushort port, string name)
        : base(name, host, port, CommandStringFormat.Ascii)
    {
    }

    internal MqttClient(string host, ushort port, string name, TimeProvider timeProvider)
        : base(name, host, port, CommandStringFormat.Ascii, timeProvider)
    {
    }

    public abstract void Send(string topic, string payload);
    public abstract void SubscribeToTopic(string topic, Action<string> handler);
}

public abstract class SshClient : IpComms
{
    protected SshClient(string host, ushort port, string name, CommandStringFormat commandStringFormat)
        : base(host, port, name, commandStringFormat)
    {
    }

    internal SshClient(string host, ushort port, string name, CommandStringFormat commandStringFormat, TimeProvider timeProvider)
        : base(host, port, name, commandStringFormat, timeProvider)
    {
    }
}

public abstract class TcpClient : IpComms
{
    protected TcpClient(string host, ushort port, string name, CommandStringFormat commandStringFormat)
        : base(host, port, name, commandStringFormat)
    {
    }

    internal TcpClient(string host, ushort port, string name, CommandStringFormat commandStringFormat, TimeProvider timeProvider)
        : base(host, port, name, commandStringFormat, timeProvider)
    {
    }
}

public abstract class UdpClient : IpComms
{
    protected UdpClient(string host, ushort port, string name, CommandStringFormat commandStringFormat)
        : base(host, port, name, commandStringFormat)
    {
    }

    internal UdpClient(string host, ushort port, string name, CommandStringFormat commandStringFormat, TimeProvider timeProvider)
        : base(host, port, name, commandStringFormat, timeProvider)
    {
    }
}

public abstract class WebSocketClient(string host, ushort port, string name, CommandStringFormat commandStringFormat)
    : IpComms(host, port, name, commandStringFormat);

public interface IWakeOnLan
{
    public void Wake(string mac);
}