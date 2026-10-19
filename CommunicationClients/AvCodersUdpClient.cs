using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Core_UdpClient = AVCoders.Core.UdpClient;
using UdpClient = System.Net.Sockets.UdpClient;

namespace AVCoders.CommunicationClients;

public class AvCodersUdpClient : Core_UdpClient
{
    private static readonly TimeSpan SocketRetryInterval = TimeSpan.FromSeconds(5);
    private UdpClient? _client;
    private readonly ConcurrentQueue<QueuedPayload<(byte[] Bytes, string? Message)>> _sendQueue = new();

    public AvCodersUdpClient(string ipAddress, ushort port, string name, CommandStringFormat commandStringFormat) :
        base(ipAddress, port, name, commandStringFormat)
    {
        _client = CreateClient();
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
                var client = new UdpClient(Host, Port);
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
                LogException(e);
                ReportConnectionFailure(DescribeConnectionError(e));
                ConnectionState = ConnectionState.Disconnected;
            }
        }
        return null;
    }

    protected override async Task Receive(CancellationToken token)
    {
        using (PushProperties("Receive"))
        {
            var client = _client;
            try
            {
                if (client is not { Available: > 0 })
                {
                    await Task.Delay(1100, token);
                    return;
                }

                IPEndPoint? sender = null;
                var received = client.Receive(ref sender);
                var response = CommandStringFormat == CommandStringFormat.Ascii
                    ? Encoding.ASCII.GetString(received)
                    : ConvertByteArrayToString(received);
                InvokeResponseHandlers(response, received);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                LogException(e);
                if (client == _client)
                    Reconnect();
            }
        }
    }

    protected override async Task ProcessSendQueue(CancellationToken token)
    {
        if (_client == null)
        {
            await Task.Delay(500, token);
        }
        else
        {
            while (_sendQueue.TryDequeue(out var item))
            {
                using (Trigger.Resume(item.TriggerId))
                {
                    var age = (DateTimeOffset.UtcNow - item.Timestamp).TotalSeconds;
                    if (Math.Abs(age) >= QueueTimeout)
                    {
                        using (PushProperties("ProcessSendQueue"))
                            LogWarning(
                                "Dropping queued message due to timeout. Age: {Age}s, Timeout: {Timeout}s",
                                age, QueueTimeout);
                        continue;
                    }
                    try
                    {
                        await _client.SendAsync(item.Payload.Bytes, token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        using (PushProperties("ProcessSendQueue"))
                            LogException(e, "Failed to send a queued message, dropping it");
                        continue;
                    }
                    InvokeRequestHandlers(item.Payload.Bytes);
                    if (item.Payload.Message != null)
                        InvokeRequestHandlers(item.Payload.Message);
                }
            }
            await Task.Delay(1100, token);
        }
    }

    protected override async Task CheckConnectionState(CancellationToken token)
    {
        using (PushProperties("CheckConnectionState"))
        {
            await Task.Delay(SocketRetryInterval, token);
            if (ConnectionState is not (ConnectionState.Connected or ConnectionState.Connecting))
                _client = CreateClient();
        }
    }

    public override void Connect()
    {
        DesiredConnectionState = ConnectionState.Connected;
        AddEvent(EventType.Connection, "Connect requested");
        _sendQueue.Clear();
        Reconnect();
        ConnectionStateWorker.Restart();
        ReceiveThreadWorker.Restart();
        SendQueueWorker.Restart();
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
        var oldClient = _client;
        _client = null;
        oldClient?.Close();
        ConnectionState = ConnectionState.Disconnected;
        _client = CreateClient();
    }

    public override void Disconnect()
    {
        DesiredConnectionState = ConnectionState.Disconnected;
        AddEvent(EventType.Connection, "Disconnect requested");
        ReceiveThreadWorker.Stop();
        ConnectionStateWorker.Stop();
        SendQueueWorker.Stop();
        ConnectionState = ConnectionState.Disconnecting;
        _client?.Close();
        _client = null;
        _sendQueue.Clear();
        ClearConnectionFailures();
        ConnectionState = ConnectionState.Disconnected;
    }

    public override void Send(byte[] bytes) => Send(bytes, null);

    public override void Send(string message) => Send(Bytes.FromString(message), message);

    private void Send(byte[] bytes, string? message)
    {
        using (PushProperties("Send"))
        {
            if (DesiredConnectionState == ConnectionState.Disconnected)
            {
                LogDebug("Not sending, the client was disconnected on request");
                return;
            }

            try
            {
                if (_client == null || !_sendQueue.IsEmpty)
                {
                    EnqueueWithCap(bytes, message);
                    return;
                }

                _client.Send(bytes, bytes.Length);
            }
            catch (Exception e)
            {
                LogException(e);
                return;
            }
            InvokeRequestHandlers(bytes);
            if (message != null)
                InvokeRequestHandlers(message);
        }
    }

    private void EnqueueWithCap(byte[] bytes, string? message)
    {
        if (_sendQueue.Count >= MaxQueueSize && _sendQueue.TryDequeue(out var dropped))
        {
            using (Trigger.Resume(dropped.TriggerId))
            using (PushProperties("EnqueueWithCap"))
                LogWarning("Send queue full, dropping oldest message. MaxQueueSize: {MaxQueueSize}", MaxQueueSize);
        }
        _sendQueue.Enqueue(new QueuedPayload<(byte[] Bytes, string? Message)>(DateTimeOffset.UtcNow, (bytes, message), Trigger.Id));
    }
}