using System.Net.Sockets;
using System.Text;
using Core_TcpClient = AVCoders.Core.TcpClient;
using TcpClient = System.Net.Sockets.TcpClient;

namespace AVCoders.CommunicationClients;

public class AvCodersTcpClient : Core_TcpClient
{
    private readonly object _clientLock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly LinkedList<QueuedPayload<(byte[] Bytes, string? Message)>> _sendQueue = new();
    private const int InitialBackoffMs = 1000;
    private int _reconnectBackoffMs = InitialBackoffMs;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ConnectedCheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);
    private ushort _receiveBufferSize = 2048;

    public AvCodersTcpClient(string host, ushort port, string name, CommandStringFormat commandStringFormat) :
        base(host, port, name, commandStringFormat)
    {
        ConnectionState = ConnectionState.Unknown;
        ConnectionStateWorker.Restart();
        ReceiveThreadWorker.Restart();
        SendQueueWorker.Restart();
    }

    protected override async Task Receive(CancellationToken token)
    {
        using (PushProperties("Receive"))
        {
            TcpClient? clientLocal;
            NetworkStream? streamLocal;
            lock (_clientLock)
            {
                clientLocal = _client;
                streamLocal = _stream;
            }

            if (streamLocal == null || !IsConnected())
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token);
                return;
            }

            try
            {
                streamLocal.ReadTimeout = (int)ReadTimeout.TotalMilliseconds;
                byte[] buffer = new byte[_receiveBufferSize];
                var bytesRead = await streamLocal.ReadAsync(buffer.AsMemory(0, buffer.Length), token);

                if (bytesRead == 0)
                {
                    Reconnect(clientLocal);
                    return;
                }

                var received = buffer.Take(bytesRead).ToArray();
                var response = CommandStringFormat == CommandStringFormat.Ascii
                    ? Encoding.Latin1.GetString(received)
                    : ConvertByteArrayToString(received);
                InvokeResponseHandlers(response, received);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception e)
            {
                LogException(e);
                Reconnect(clientLocal);
            }
        }
    }

    protected override async Task CheckConnectionState(CancellationToken token)
    {
        using (PushProperties("CheckConnectionState"))
        {
            if (IsConnected())
            {
                ConnectionState = ConnectionState.Connected;

                var current = CurrentClient();
                try
                {
                    if (!IsSocketHealthy(current?.Client))
                    {
                        Reconnect(current);
                    }
                }
                catch (Exception e)
                {
                    LogException(e);
                    Reconnect(current);
                }

                await Task.Delay(ConnectedCheckInterval, token);
                return;
            }

            ConnectionState = ConnectionState.Connecting;

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(5));

                var attempt = new TcpClient();
                await attempt.ConnectAsync(Host, Port, cts.Token);
                if (token.IsCancellationRequested)
                {
                    LogDebug("Discarding the new connection, the connection worker was stopped");
                    attempt.Dispose();
                    ConnectionState = ConnectionState.Disconnected;
                    return;
                }

                var newStream = attempt.GetStream();
                newStream.ReadTimeout = (int)ReadTimeout.TotalMilliseconds;
                newStream.WriteTimeout = (int)WriteTimeout.TotalMilliseconds;
                ConfigureKeepAlive(attempt);

                TcpClient? oldClient = null;
                NetworkStream? oldStream = null;

                lock (_clientLock)
                {
                    oldStream = _stream;
                    oldClient = _client;
                    _client = attempt;
                    _stream = newStream;
                }

                CloseQuietly(oldStream, oldClient);

                _reconnectBackoffMs = InitialBackoffMs;
                ConnectionState = ConnectionState.Connected;
                ReceiveThreadWorker.Restart();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                LogDebug("The connection attempt was cancelled, the connection worker was stopped");
                ConnectionState = ConnectionState.Disconnected;
                return;
            }
            catch (Exception e)
            {
                LogException(e);
                ReportConnectionFailure(DescribeConnectionError(e));
                ConnectionState = ConnectionState.Disconnected;
                await WaitWithBackoff(token);
                return;
            }

            await Task.Delay(ConnectedCheckInterval, token);
        }
    }

    private async Task WaitWithBackoff(CancellationToken token)
    {
        var jitter = Random.Shared.Next(-200, 200);
        var delay = Math.Min(_reconnectBackoffMs + jitter, (int)MaxBackoff.TotalMilliseconds);
        _reconnectBackoffMs = Math.Min(_reconnectBackoffMs * 2, (int)MaxBackoff.TotalMilliseconds);
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(500, delay)), token);
    }

    protected override async Task ProcessSendQueue(CancellationToken token)
    {
        if (!IsConnected())
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            return;
        }

        while (Dequeue() is { } item)
        {
            using (Trigger.Resume(item.TriggerId))
            {
                // Drop stale items
                var age = (DateTimeOffset.UtcNow - item.Timestamp).TotalSeconds;
                if (Math.Abs(age) >= QueueTimeout)
                {
                    using (PushProperties("ProcessSendQueue"))
                        LogWarning(
                            "Dropping queued message due to timeout. Age: {Age}s, Timeout: {Timeout}s",
                            age, QueueTimeout);
                    continue;
                }

                var connection = CurrentClient();
                try
                {
                    await SafeWriteAsync(connection, item.Payload.Bytes, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    using (PushProperties("ProcessSendQueue"))
                        LogDebug("Stopped sending the queue, the send worker was stopped");
                    throw;
                }
                catch (Exception e)
                {
                    LogException(e);
                    lock (_sendQueue)
                        _sendQueue.AddFirst(item with { Timestamp = DateTimeOffset.UtcNow });
                    Reconnect(connection);
                    break;
                }
                InvokeRequestHandlers(item.Payload.Bytes);
                if (item.Payload.Message != null)
                    InvokeRequestHandlers(item.Payload.Message);
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(2), token);
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

    public override void Send(string message) => Send(Bytes.FromString(message), message);

    public override void Send(byte[] bytes) => Send(bytes, null);

    private void Send(byte[] bytes, string? message)
    {
        using (PushProperties("Send"))
        {
            if (DesiredConnectionState == ConnectionState.Disconnected)
            {
                LogDebug("Not sending, the client was disconnected on request");
                return;
            }

            if (!IsConnected() || HasQueuedMessages())
            {
                EnqueueWithCap(bytes, message);
                return;
            }

            var connection = CurrentClient();
            try
            {
                SafeWriteAsync(connection, bytes, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                LogException(e);
                EnqueueWithCap(bytes, message);
                Reconnect(connection);
                return;
            }
            InvokeRequestHandlers(bytes);
            if (message != null)
                InvokeRequestHandlers(message);
        }
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
            _sendQueue.AddLast(new QueuedPayload<(byte[] Bytes, string? Message)>(DateTimeOffset.UtcNow, (bytes, message), Trigger.Id));
        }

        if (dropped == null)
            return;
        using (Trigger.Resume(dropped.TriggerId))
        using (PushProperties("EnqueueWithCap"))
            LogWarning("Send queue full, dropping oldest message. MaxQueueSize: {MaxQueueSize}", MaxQueueSize);
    }

    public override void Connect()
    {
        DesiredConnectionState = ConnectionState.Connected;
        AddEvent(EventType.Connection, "Connect requested");
        lock (_sendQueue)
            _sendQueue.Clear();
        _reconnectBackoffMs = InitialBackoffMs;
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

        _reconnectBackoffMs = InitialBackoffMs;
        if (ConnectionState == ConnectionState.Connecting)
        {
            using (PushProperties("Reconnect"))
                LogDebug("Not restarting the connection attempt that is already running");
            return;
        }

        if (IsConnected())
            Reconnect(CurrentClient());
        ConnectionStateWorker.Restart();
    }

    private void Reconnect(TcpClient? failed)
    {
        if (DesiredConnectionState == ConnectionState.Disconnected)
        {
            using (PushProperties("Reconnect"))
                LogDebug("Not reconnecting, the client was disconnected on request");
            return;
        }

        if (!TryTakeConnection(failed, out var failedStream))
        {
            using (PushProperties("Reconnect"))
                LogDebug("Not reconnecting, the connection was already closed");
            return;
        }

        ConnectionState = ConnectionState.Disconnecting;
        CloseQuietly(failedStream, failed);
        ConnectionState = ConnectionState.Disconnected;
    }

    private bool TryTakeConnection(TcpClient? connection, out NetworkStream? stream)
    {
        lock (_clientLock)
        {
            stream = _stream;
            if (connection == null || connection != _client)
                return false;
            _stream = null;
            _client = null;
            return true;
        }
    }

    public override void Disconnect()
    {
        DesiredConnectionState = ConnectionState.Disconnected;
        AddEvent(EventType.Connection, "Disconnect requested");
        ConnectionStateWorker.Stop();
        ReceiveThreadWorker.Stop();
        SendQueueWorker.Stop();
        ConnectionState = ConnectionState.Disconnecting;
        DestroyClientSafely();
        lock (_sendQueue)
            _sendQueue.Clear();
        ClearConnectionFailures();
        ConnectionState = ConnectionState.Disconnected;
    }

    private void ConfigureKeepAlive(TcpClient client)
    {
        try
        {
            Socket socket = client.Client;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            TrySetTcpOption(socket, SocketOptionName.TcpKeepAliveTime, 10);
            TrySetTcpOption(socket, SocketOptionName.TcpKeepAliveInterval, 3);
            TrySetTcpOption(socket, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (SocketException e)
        {
            LogException(e, "There was an error setting socket keepalive options");
        }
        catch (PlatformNotSupportedException e)
        {
            LogException(e, "This platform doesn't support socket keepalive options");
        }
    }

    private void TrySetTcpOption(Socket socket, SocketOptionName optionName, int value)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Tcp, optionName, value);
        }
        catch (PlatformNotSupportedException e)
        {
            LogException(e, $"KeepAlive option {optionName} not supported on this platform");
        }
        catch (SocketException e)
        {
            LogException(e, $"Failed to set KeepAlive option {optionName}");
        }
    }

    private void DestroyClientSafely()
    {
        TcpClient? toCloseClient = null;
        NetworkStream? toCloseStream = null;

        lock (_clientLock)
        {
            toCloseStream = _stream;
            toCloseClient = _client;
            _stream = null;
            _client = null;
            // Worker handles client / socket creation
        }

        CloseQuietly(toCloseStream, toCloseClient);
    }

    private static void CloseQuietly(NetworkStream? stream, TcpClient? client)
    {
        try { stream?.Close(); } catch { /* ignore */ }
        try { stream?.Dispose(); } catch { /* ignore */ }
        try { client?.Close(); } catch { /* ignore */ }
        try { client?.Dispose(); } catch { /* ignore */ }
    }

    private bool IsConnected()
    {
        Socket? sock = GetSocket();
        if (sock == null) return false;
        try
        {
            if (!sock.Connected) return false;
            bool readable = sock.Poll(0, SelectMode.SelectRead);
            bool hasData = sock.Available > 0;
            if (readable && !hasData) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private TcpClient? CurrentClient()
    {
        lock (_clientLock)
        {
            return _client;
        }
    }

    private Socket? GetSocket()
    {
        lock (_clientLock)
        {
            return _client?.Client;
        }
    }

    private static bool IsSocketHealthy(Socket? socket)
    {
        if (socket == null) return false;
        try
        {
            bool readable = socket.Poll(0, SelectMode.SelectRead);
            bool hasData = socket.Available > 0;
            if (readable && !hasData) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task SafeWriteAsync(TcpClient? connection, byte[] payload, CancellationToken token)
    {
        if (connection == null || !IsSocketHealthy(connection.Client))
            throw new SocketException((int)SocketError.NotConnected);
        var streamLocal = connection.GetStream();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(WriteTimeout);
        try
        {
            await _writeLock.WaitAsync(timeoutCts.Token);
            try
            {
                await streamLocal.WriteAsync(payload.AsMemory(0, payload.Length), timeoutCts.Token);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException($"Write timed out after {WriteTimeout.TotalSeconds}s");
        }
    }

    public void SetReceiveBufferSize(ushort bufferSize)
    {
        if (bufferSize < 1024)
            throw new ArgumentOutOfRangeException(nameof(bufferSize), "Buffer size must be at least 1024 bytes.");
        _receiveBufferSize = bufferSize;
    }
}
