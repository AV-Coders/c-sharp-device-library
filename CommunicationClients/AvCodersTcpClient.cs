using System.Net.Sockets;
using System.Text;
using Serilog;
using Serilog.Context;
using Core_TcpClient = AVCoders.Core.TcpClient;
using TcpClient = System.Net.Sockets.TcpClient;

namespace AVCoders.CommunicationClients;

public class AvCodersTcpClient : Core_TcpClient
{
    private readonly object _clientLock = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly Queue<QueuedPayload<byte[]>> _sendQueue = new();
    private int _reconnectBackoffMs = 1000; // starts at 1 second
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(20);
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
            NetworkStream? streamLocal;
            lock (_clientLock)
            {
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
                    Reconnect();
                    return;
                }
                
                string response = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                InvokeResponseHandlers(response, buffer.Take(bytesRead).ToArray());
            }
            catch (IOException e)
            {
                LogException(e);
                Reconnect();
            }
            catch (ObjectDisposedException e)
            {
                LogException(e);
                Reconnect();
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception e)
            {
                LogException(e);
                Reconnect();
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

                try
                {
                    if (!IsSocketHealthy(GetSocket()))
                    {
                        Reconnect();
                    }
                }
                catch (Exception e)
                {
                    LogException(e);
                    Reconnect();
                }

                await Task.Delay(TimeSpan.FromSeconds(15), token);
                return;
            }

            ConnectionState = ConnectionState.Connecting;

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(5));

                var attempt = new TcpClient();
                await attempt.ConnectAsync(Host, Port, cts.Token);

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

                try { oldStream?.Close(); } catch { /* ignore */ }
                try { oldStream?.Dispose(); } catch { /* ignore */ }
                try { oldClient?.Close(); } catch { /* ignore */ }
                try { oldClient?.Dispose(); } catch { /* ignore */ }

                _reconnectBackoffMs = 1000; // reset backoff
                ConnectionState = ConnectionState.Connected;
                ReceiveThreadWorker.Restart();
            }
            catch (OperationCanceledException e)
            {
                LogException(e);
                ConnectionState = ConnectionState.Disconnected;
            }
            catch (SocketException e)
            {
                LogException(e);
                ConnectionState = ConnectionState.Disconnected;
            }
            catch (IOException e)
            {
                LogException(e);
                ConnectionState = ConnectionState.Disconnected;
            }
            catch (ObjectDisposedException e)
            {
                LogException(e);
                ConnectionState = ConnectionState.Disconnected;
            }
            catch (Exception e)
            {
                LogException(e);
                ConnectionState = ConnectionState.Disconnected;
            }

            // jittered exponential backoff
            var jitter = Random.Shared.Next(-200, 200);
            var delay = Math.Min(_reconnectBackoffMs + jitter, (int)MaxBackoff.TotalMilliseconds);
            _reconnectBackoffMs = Math.Min(_reconnectBackoffMs * 2, (int)MaxBackoff.TotalMilliseconds);
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(500, delay)), token);
        }
    }
    
    protected override async Task ProcessSendQueue(CancellationToken token)
    {
        if (!IsConnected())
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            return;
        }

        while (_sendQueue.Count > 0)
        {
            var item = _sendQueue.Dequeue();

            // Drop stale items
            if (Math.Abs((DateTime.Now - item.Timestamp).TotalSeconds) >= QueueTimeout)
                continue;

            try
            {
                await SafeWriteAsync(item.Payload, token);
            }
            catch (IOException e)
            {
                LogException(e);
                _sendQueue.Enqueue(item); // requeue if still within timeout
                Reconnect();
                break;
            }
            catch (ObjectDisposedException e)
            {
                LogException(e);
                _sendQueue.Enqueue(item);
                Reconnect();
                break;
            }
            catch (SocketException e)
            {
                LogException(e);
                _sendQueue.Enqueue(item);
                Reconnect();
                break;
            }
            catch (Exception e)
            {
                LogException(e);
                _sendQueue.Enqueue(item);
                Reconnect();
                break;
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(2), token);
    }

    public override void Send(string message)
    {
        Send(Bytes.FromString(message));
        InvokeRequestHandlers(message);
    }

    public override void Send(byte[] bytes)
    {
        using (PushProperties("Send"))
        {
            if (IsConnected())
            {
                try
                {
                    SafeWriteAsync(bytes, CancellationToken.None).GetAwaiter().GetResult();
                    InvokeRequestHandlers(bytes);
                    return;
                }
                catch (IOException e)
                {
                    LogException(e);
                    _sendQueue.Enqueue(new QueuedPayload<byte[]>(DateTime.UtcNow, bytes));
                    Reconnect();
                }
                catch (ObjectDisposedException e)
                {
                    LogException(e);
                    _sendQueue.Enqueue(new QueuedPayload<byte[]>(DateTime.UtcNow, bytes));
                    Reconnect();
                }
                catch (SocketException e)
                {
                    LogException(e);
                    _sendQueue.Enqueue(new QueuedPayload<byte[]>(DateTime.UtcNow, bytes));
                    Reconnect();
                }
                catch (Exception e)
                {
                    LogException(e);
                    _sendQueue.Enqueue(new QueuedPayload<byte[]>(DateTime.UtcNow, bytes));
                    Reconnect();
                }
            }

            _sendQueue.Enqueue(new QueuedPayload<byte[]>(DateTime.UtcNow, bytes));
        }
    }

    public override void Connect()
    {
        _sendQueue.Clear();
        ConnectionStateWorker.Restart();
    }

    public override void Reconnect()
    {
        ConnectionState = ConnectionState.Disconnecting;
        DestroyClientSafely();
        ConnectionState = ConnectionState.Disconnected;
        // Worker handles client / socket creation
    }
    
    public override void Disconnect()
    {
        ConnectionStateWorker.Stop();
        ConnectionState = ConnectionState.Disconnecting;
        DestroyClientSafely();
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

        try { toCloseStream?.Close(); } catch { /* ignore */ }
        try { toCloseStream?.Dispose(); } catch { /* ignore */ }
        try { toCloseClient?.Close(); } catch { /* ignore */ }
        try { toCloseClient?.Dispose(); } catch { /* ignore */ }
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

    private async Task SafeWriteAsync(byte[] payload, CancellationToken token)
    {
        NetworkStream? streamLocal;
        Socket? socketLocal;

        lock (_clientLock)
        {
            streamLocal = _stream;
            socketLocal = _client?.Client;
        }

        if (streamLocal == null || socketLocal == null || !IsSocketHealthy(socketLocal))
            throw new SocketException((int)SocketError.NotConnected);

        streamLocal.WriteTimeout = (int)WriteTimeout.TotalMilliseconds;
        await streamLocal.WriteAsync(payload.AsMemory(0, payload.Length), token);
    }
    
    public void SetReceiveBufferSize(ushort bufferSize)
    {
        if (bufferSize < 1024)
            throw new ArgumentOutOfRangeException(nameof(bufferSize), "Buffer size must be at least 1024 bytes.");
        _receiveBufferSize = bufferSize;
    }
}