using System.Net;
using System.Net.Sockets;
using System.Text;
using Core_UdpClient = AVCoders.Core.UdpClient;
using UdpClient = System.Net.Sockets.UdpClient;

namespace AVCoders.CommunicationClients;

public class AvCodersUdpClient : Core_UdpClient
{
    private static readonly TimeSpan SocketRetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NoSocketReceiveWait = TimeSpan.FromMilliseconds(1100);
    private static readonly TimeSpan NoSocketSendWait = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SendQueueInterval = TimeSpan.FromMilliseconds(1100);
    private readonly Func<string, IPAddress[]> _resolveHost;
    private readonly object _clientLock = new();
    private UdpClient? _client;
    private readonly LinkedList<QueuedPayload<(byte[] Bytes, string? Message)>> _sendQueue = new();

    public AvCodersUdpClient(string ipAddress, ushort port, string name, CommandStringFormat commandStringFormat) :
        this(ipAddress, port, name, commandStringFormat, null, null)
    {
    }

    internal AvCodersUdpClient(string ipAddress, ushort port, string name, CommandStringFormat commandStringFormat,
        TimeProvider? timeProvider, Func<string, IPAddress[]>? resolveHost = null) :
        base(ipAddress, port, name, commandStringFormat, timeProvider ?? TimeProvider.System)
    {
        _resolveHost = resolveHost ?? Dns.GetHostAddresses;
        DesiredConnectionState = ConnectionState.Connected;
        ReplaceClient(CreateClient());
        ConnectionStateWorker.Restart();
        ReceiveThreadWorker.Restart();
        SendQueueWorker.Restart();
    }

    private UdpClient? CreateClient()
    {
        using (PushProperties("CreateClient"))
        {
            try
            {
                ConnectionState = ConnectionState.Connecting;
                var client = OpenSocket(_resolveHost(Host));
                if (DesiredConnectionState == ConnectionState.Disconnected)
                {
                    LogDebug("Discarding the new socket, the client was disconnected on request");
                    client.Close();
                    ConnectionState = ConnectionState.Disconnected;
                    return null;
                }
                ConnectionState = ConnectionState.Connected;
                return client;
            }
            catch (Exception e)
            {
                ReportConnectionFailure(e);
                ConnectionState = ConnectionState.Disconnected;
            }
        }
        return null;
    }

    private UdpClient OpenSocket(IPAddress[] addresses)
    {
        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);
        Exception? lastFailure = null;
        foreach (var address in addresses)
        {
            UdpClient? client = null;
            try
            {
                client = new UdpClient(address.AddressFamily);
                client.Connect(address, Port);
                return client;
            }
            catch (Exception e)
            {
                client?.Dispose();
                lastFailure = e;
            }
        }
        throw lastFailure!;
    }

    protected override async Task Receive(CancellationToken token)
    {
        using (PushProperties("Receive"))
        {
            var client = _client;
            if (client == null)
            {
                await Task.Delay(NoSocketReceiveWait, TimeProvider, token);
                return;
            }

            try
            {
                var result = await client.ReceiveAsync(token);
                ClearConnectionFailures();
                var received = result.Buffer;
                var response = CommandStringFormat == CommandStringFormat.Ascii
                    ? Encoding.Latin1.GetString(received)
                    : ConvertByteArrayToString(received);
                InvokeResponseHandlers(response, received);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e) when (client != _client)
            {
                LogDebug("Stopped receiving on the replaced socket: {Message}", e.Message);
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused)
            {
                ReportConnectionFailure(ConnectionReasonCategory.Refused, DescribeConnectionError(e), e);
            }
            catch (Exception e)
            {
                LogException(e);
                Reconnect();
            }
        }
    }

    protected override async Task ProcessSendQueue(CancellationToken token)
    {
        var client = _client;
        if (client == null)
        {
            await Task.Delay(NoSocketSendWait, TimeProvider, token);
        }
        else
        {
            while (Dequeue() is { } item)
            {
                using (Trigger.Resume(item.TriggerId))
                {
                    var age = (TimeProvider.GetUtcNow() - item.Timestamp).TotalSeconds;
                    var timeout = QueueTimeoutFor(item.WaitsForTheReconnect);
                    if (Math.Abs(age) >= timeout)
                    {
                        using (PushProperties("ProcessSendQueue"))
                            LogWarning(
                                "Dropping queued message due to timeout. Age: {Age}s, Timeout: {Timeout}s",
                                age, timeout);
                        continue;
                    }
                    try
                    {
                        await client.SendAsync(item.Payload.Bytes, token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        LogException(e);
                        lock (_sendQueue)
                            _sendQueue.AddFirst(item with { Timestamp = TimeProvider.GetUtcNow() });
                        ReconnectIfCurrent(client);
                        break;
                    }
                    InvokeRequestHandlers(item.Payload.Bytes);
                    if (item.Payload.Message != null)
                        InvokeRequestHandlers(item.Payload.Message);
                }
            }
            await Task.Delay(SendQueueInterval, TimeProvider, token);
        }
    }

    private QueuedPayload<(byte[] Bytes, string? Message)>? Dequeue()
    {
        lock (_sendQueue)
        {
            var first = _sendQueue.First;
            if (first == null)
                return null;
            _sendQueue.RemoveFirst();
            return first.Value;
        }
    }

    private bool HasQueuedMessages()
    {
        lock (_sendQueue)
            return _sendQueue.Count > 0;
    }

    protected override async Task CheckConnectionState(CancellationToken token)
    {
        using (PushProperties("CheckConnectionState"))
        {
            await Task.Delay(SocketRetryInterval, TimeProvider, token);
            if (ConnectionState is not (ConnectionState.Connected or ConnectionState.Connecting))
                ReplaceClient(CreateClient());
        }
    }

    public override void Connect()
    {
        lock (ConnectionRequestLock)
        {
            RecordRequestedConnect();
            if (!ReconnectingForSend)
                lock (_sendQueue)
                    _sendQueue.Clear();
            DesiredConnectionState = ConnectionState.Connected;
            AddEvent(EventType.Connection, "Connect requested");
            Reconnect();
            ConnectionStateWorker.Restart();
            ReceiveThreadWorker.Restart();
            SendQueueWorker.Restart();
        }
    }

    public override void Reconnect()
    {
        if (DesiredConnectionState == ConnectionState.Disconnected)
        {
            using (PushProperties("Reconnect"))
                LogDebug("Not reconnecting, the client was disconnected on request");
            return;
        }
        ConnectionState = ConnectionState.Disconnecting;
        ReplaceClient(null);
        ConnectionState = ConnectionState.Disconnected;
        ReplaceClient(CreateClient());
    }

    private void ReconnectIfCurrent(UdpClient client)
    {
        lock (_clientLock)
        {
            if (client != _client)
                return;
        }
        Reconnect();
    }

    private void ReplaceClient(UdpClient? client)
    {
        UdpClient? previous;
        lock (_clientLock)
        {
            previous = _client;
            _client = client;
        }
        previous?.Close();
    }

    public override void Disconnect()
    {
        lock (ConnectionRequestLock)
        {
            RecordRequestedDisconnect();
            DesiredConnectionState = ConnectionState.Disconnected;
            AddEvent(EventType.Connection, "Disconnect requested");
            ReceiveThreadWorker.Stop();
            ConnectionStateWorker.Stop();
            SendQueueWorker.Stop();
            ConnectionState = ConnectionState.Disconnecting;
            ReplaceClient(null);
            lock (_sendQueue)
                _sendQueue.Clear();
            ClearConnectionFailures();
            ConnectionState = ConnectionState.Disconnected;
        }
    }

    public override void Send(byte[] bytes) => Send(bytes, null);

    public override void Send(string message) => Send(Bytes.FromString(message), message);

    private void Send(byte[] bytes, string? message)
    {
        using (PushProperties("Send"))
        {
            if (DesiredConnectionState == ConnectionState.Disconnected)
            {
                if (!ReconnectForSend(() => SendOrQueue(bytes, message)))
                    LogDebug("Not sending, the client was disconnected on request");
                return;
            }

            SendOrQueue(bytes, message);
        }
    }

    private void SendOrQueue(byte[] bytes, string? message)
    {
        var client = _client;
        if (client == null || HasQueuedMessages())
        {
            EnqueueWithCap(bytes, message);
            return;
        }

        try
        {
            client.Send(bytes, bytes.Length);
        }
        catch (Exception e)
        {
            LogException(e);
            EnqueueWithCap(bytes, message);
            _ = Task.Run(() => ReconnectIfCurrent(client));
            return;
        }
        InvokeRequestHandlers(bytes);
        if (message != null)
            InvokeRequestHandlers(message);
    }

    private void EnqueueWithCap(byte[] bytes, string? message)
    {
        QueuedPayload<(byte[] Bytes, string? Message)>? dropped = null;
        lock (_sendQueue)
        {
            if (_sendQueue.Count >= MaxQueueSize && _sendQueue.First is { } oldest)
            {
                dropped = oldest.Value;
                _sendQueue.RemoveFirst();
            }
            _sendQueue.AddLast(new QueuedPayload<(byte[] Bytes, string? Message)>(TimeProvider.GetUtcNow(), (bytes, message), Trigger.Id, WaitsForTheReconnect()));
        }

        if (dropped == null)
            return;
        using (Trigger.Resume(dropped.TriggerId))
        using (PushProperties("EnqueueWithCap"))
            LogWarning("Send queue full, dropping oldest message. MaxQueueSize: {MaxQueueSize}", MaxQueueSize);
    }
}
