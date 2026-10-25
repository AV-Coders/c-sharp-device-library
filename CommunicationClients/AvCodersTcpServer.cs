using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Core_TcpClient = AVCoders.Core.TcpClient;
using TcpClient = System.Net.Sockets.TcpClient;

namespace AVCoders.CommunicationClients;

public class AvCodersTcpServer : Core_TcpClient
{
    private const int InitialBindBackoffMs = 1000;
    private TcpListener? _server;
    private volatile bool _listening;
    private int _bindBackoffMs = InitialBindBackoffMs;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<Guid, TcpClient> _clients = new();
    private readonly object _stateLock = new();
    private CancellationTokenSource _clientsCancellation = new();

    public AvCodersTcpServer(ushort port, string name, CommandStringFormat commandStringFormat)
        : base("Any", port, name, commandStringFormat)
    {
        ConnectionState = ConnectionState.Connecting;

        ReceiveThreadWorker.Restart(); // Used to connect to new clients
        ConnectionStateWorker.Restart(); // Binds the listener, then monitors clients
    }

    private bool TryStartListener()
    {
        using (PushProperties("TryStartListener"))
        {
            TcpListener listener = new TcpListener(IPAddress.Any, Port);
            try
            {
                listener.ExclusiveAddressUse = false;
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Start();

                lock (_stateLock)
                {
                    if (DesiredConnectionState == ConnectionState.Disconnected)
                    {
                        LogDebug("Discarding the new listener, the server was disconnected on request");
                        listener.Stop();
                        return false;
                    }
                    _server = listener;
                    _listening = true;
                }
                _bindBackoffMs = InitialBindBackoffMs;
                ClearConnectionFailures();
                return true;
            }
            catch (Exception e)
            {
                LogException(e, $"Failed to bind TCP server to port {Port}");
                ReportConnectionFailure(DescribeConnectionError(e));
                try
                {
                    listener.Server.Dispose();
                }
                catch (Exception disposeException)
                {
                    LogException(disposeException);
                }
                return false;
            }
        }
    }

    private async Task BindBackoffDelay(CancellationToken token)
    {
        // jittered exponential backoff
        var jitter = Random.Shared.Next(-200, 200);
        var delay = Math.Min(_bindBackoffMs + jitter, (int)MaxBackoff.TotalMilliseconds);
        _bindBackoffMs = Math.Min(_bindBackoffMs * 2, (int)MaxBackoff.TotalMilliseconds);
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(500, delay)), token);
    }

    public override void Send(string message) => Send(Bytes.FromString(message), message);

    public override void Send(byte[] bytes) => Send(bytes, null);

    private void Send(byte[] bytes, string? message)
    {
        using (PushProperties("Send"))
        {
            var written = false;
            foreach (var kvp in _clients)
            {
                TcpClient client = kvp.Value;
                try
                {
                    if (!client.Connected || !client.GetStream().CanWrite)
                        continue;
                    client.GetStream().Write(bytes);
                    written = true;
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    LogException(e, $"Sending to client {kvp.Key} failed, dropping it");
                    if (_clients.TryRemove(kvp.Key, out _))
                        client.Dispose();
                }
            }

            if (!written)
                return;
            InvokeRequestHandlers(bytes);
            if (message != null)
                InvokeRequestHandlers(message);
        }
    }

    private async Task HandleClientAsync(TcpClient client, Guid clientId, CancellationToken token)
    {
        using (PushProperties("HandleClientAsync"))
        {
            try
            {
                await using NetworkStream stream = client.GetStream();
                byte[] buffer = new byte[1024];
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token)) != 0)
                {
                    // Ignore probe bytes sent by TCP Client
                    if (bytesRead == 1 && buffer[0] == 0)
                        continue;
                    byte[] received = buffer.Take(bytesRead).ToArray();
                    string response = CommandStringFormat == CommandStringFormat.Ascii
                        ? Encoding.Latin1.GetString(received)
                        : ConvertByteArrayToString(received);
                    using (Trigger.Begin())
                        InvokeResponseHandlers(response, received);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                LogDebug("Dropping client {ClientId}, the server was disconnected", clientId);
            }
            catch (Exception e)
            {
                LogException(e);
            }
            finally
            {
                // Clean up this specific client when HandleClientAsync exits
                _clients.TryRemove(clientId, out _);
                client.Dispose();
                LogDebug("Client {ClientId} disconnected and removed", clientId);
                SetConnectionStateByClients();
            }
        }
    }

    protected override Task ProcessSendQueue(CancellationToken token) => SendQueueWorker.Stop();

    protected override async Task Receive(CancellationToken token)
    {
        using (PushProperties("Receive"))
        {
            TcpListener? server = _server;
            if (!_listening || server == null)
            {
                // The ConnectionStateWorker is responsible for (re)binding the listener
                await Task.Delay(TimeSpan.FromSeconds(1), token);
                return;
            }

            try
            {
                TcpClient client = await server.AcceptTcpClientAsync(token);
                client.SendTimeout = (int)WriteTimeout.TotalMilliseconds;
                Guid clientId = Guid.NewGuid();
                CancellationToken clientToken;
                lock (_stateLock)
                {
                    if (DesiredConnectionState == ConnectionState.Disconnected)
                    {
                        LogDebug("Discarding the new client, the server was disconnected on request");
                        client.Dispose();
                        return;
                    }
                    clientToken = _clientsCancellation.Token;
                    _clients.TryAdd(clientId, client);
                }
                SetConnectionStateByClients();
                IPEndPoint? remoteIpEndPoint = client.Client.RemoteEndPoint as IPEndPoint ?? null;
                LogDebug("Added client {ClientId} - {IpAddress}", clientId, remoteIpEndPoint?.Address);
                _ = HandleClientAsync(client, clientId, clientToken);
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
            catch (SocketException e)
            {
                HandleDeadListener(e, token);
            }
            catch (ObjectDisposedException e)
            {
                HandleDeadListener(e, token);
            }
            catch (InvalidOperationException e)
            {
                HandleDeadListener(e, token);
            }
        }
    }

    private void HandleDeadListener(Exception e, CancellationToken token)
    {
        if (token.IsCancellationRequested || DesiredConnectionState == ConnectionState.Disconnected)
        {
            LogDebug("The listener closed while the server was stopping: {Message}", e.Message);
            return;
        }
        LogException(e, "Listener failed, it will be rebound");
        CloseListener();
        ReportConnectionFailure(DescribeConnectionError(e));
        SetConnectionStateByClients();
        ConnectionStateWorker.Restart();
    }

    private void SetConnectionStateByClients()
    {
        ConnectionState published;
        lock (_stateLock)
        {
            if (DesiredConnectionState == ConnectionState.Disconnected)
                return;
            published = StateForClients();
        }
        ConnectionState = published;

        ConnectionState current;
        lock (_stateLock)
        {
            current = DesiredConnectionState == ConnectionState.Disconnected
                ? ConnectionState.Disconnected
                : StateForClients();
        }
        if (current != published)
            ConnectionState = current;
    }

    private ConnectionState StateForClients() => (_listening, _clients.IsEmpty) switch
    {
        (true, false) => ConnectionState.Connected,
        (true, true) => ConnectionState.Disconnected,
        (false, false) => ConnectionState.Degraded,
        (false, true) => ConnectionState.Error,
    };

    private void CloseListener()
    {
        TcpListener? closed;
        lock (_stateLock)
        {
            _listening = false;
            closed = _server;
            _server = null;
        }
        try
        {
            closed?.Server.Dispose();
        }
        catch (Exception disposeException)
        {
            LogException(disposeException);
        }
    }

    private void DropClients()
    {
        CancellationTokenSource dropped;
        lock (_stateLock)
        {
            dropped = _clientsCancellation;
            _clientsCancellation = new CancellationTokenSource();
        }
        dropped.Cancel();
        dropped.Dispose();
    }

    protected override async Task CheckConnectionState(CancellationToken token)
    {
        using (PushProperties("CheckConnectionState"))
        {
            var disconnectedClients = _clients.Where(kvp => !kvp.Value.Connected).ToList();
            foreach (var kvp in disconnectedClients)
            {
                if (_clients.TryRemove(kvp.Key, out var client))
                {
                    LogDebug("Removing disconnected client {ClientId}", kvp.Key);
                    client.Dispose();
                }
            }

            if (!_listening && !TryStartListener())
            {
                SetConnectionStateByClients();
                await BindBackoffDelay(token);
                return;
            }

            SetConnectionStateByClients();
            await Task.Delay(TimeSpan.FromSeconds(45), token);
        }
    }

    public override void Connect()
    {
        DesiredConnectionState = ConnectionState.Connected;
        AddEvent(EventType.Connection, "Connect requested");
        _bindBackoffMs = InitialBindBackoffMs;
        ConnectionStateWorker.Restart();
        ReceiveThreadWorker.Restart();
    }

    public override void Reconnect()
    {
        if (DesiredConnectionState == ConnectionState.Disconnected)
        {
            using (PushProperties("Reconnect"))
                LogDebug("Not reconnecting, the server was disconnected on request");
            return;
        }
        ReceiveThreadWorker.Restart();
    }

    public override void Disconnect()
    {
        DesiredConnectionState = ConnectionState.Disconnected;
        AddEvent(EventType.Connection, "Disconnect requested");
        ConnectionStateWorker.Stop();
        ReceiveThreadWorker.Stop();
        ConnectionState = ConnectionState.Disconnecting;
        CloseListener();
        DropClients();
        ClearConnectionFailures();
        ConnectionState = ConnectionState.Disconnected;
    }
}