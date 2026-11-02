using System.Net;
using System.Net.Sockets;
using System.Text;
using UdpClient = System.Net.Sockets.UdpClient;

namespace AVCoders.CommunicationClients;

public class AvCodersMulticastClient : IMulticastClient
{
    private const int InitialBindBackoffMs = 1000;
    private readonly object _clientLock = new();
    private UdpClient? _client;
    private IPEndPoint? _remoteEndPoint;
    private readonly int _ttl;
    private int _bindBackoffMs = InitialBindBackoffMs;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan UnboundReceiveWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReceiveFailureWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BoundCheckInterval = TimeSpan.FromSeconds(30);

    public AvCodersMulticastClient(string ipAddress, ushort port, string name, int ttl = 1, CommandStringFormat commandStringFormat = CommandStringFormat.Ascii)
        : this(ipAddress, port, name, ttl, commandStringFormat, null)
    {
    }

    internal AvCodersMulticastClient(string ipAddress, ushort port, string name, int ttl, CommandStringFormat commandStringFormat, TimeProvider? timeProvider)
        : base(ipAddress, port, name, commandStringFormat, timeProvider ?? TimeProvider.System)
    {
        using (PushProperties("Constructor"))
        {
            ConnectionState = ConnectionState.Connecting;

            _ttl = ttl;

            ConnectionStateWorker.Restart(); // Binds the socket, retrying until it succeeds
            // This works around a race condition coming from base being called first.
            ReceiveThreadWorker.Restart();
        }
    }

    private bool TryBind()
    {
        using (PushProperties("TryBind"))
        {
            if (!IPAddress.TryParse(Host, out var multicastIp))
            {
                ReportConnectionFailure($"The multicast group '{Host}' is not an IP address, so it was not used");
                ConnectionState = ConnectionState.Error;
                return false;
            }

            var af = multicastIp.AddressFamily;
            UdpClient client = new UdpClient(af);
            try
            {
                if (af == AddressFamily.InterNetwork)
                {
                    client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, _ttl);
                }
                else if (af == AddressFamily.InterNetworkV6)
                {
                    client.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, _ttl);
                }

                client.Client.ExclusiveAddressUse = false;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                var anyAddress = af == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
                client.Client.Bind(new IPEndPoint(anyAddress, Port));

                client.JoinMulticastGroup(multicastIp);

                lock (_clientLock)
                {
                    _client = client;
                    _remoteEndPoint = new IPEndPoint(multicastIp, Port);
                }
                ConnectionState = ConnectionState.Connected;
                return true;
            }
            catch (Exception e)
            {
                LogException(e, $"Failed to bind multicast client on port {Port}");
                ReportConnectionFailure(e);
                try
                {
                    client.Dispose();
                }
                catch (Exception disposeException)
                {
                    LogException(disposeException);
                }
                ConnectionState = ConnectionState.Error;
                return false;
            }
        }
    }

    protected override async Task Receive(CancellationToken token)
    {
        using (PushProperties("Receive"))
        {
            UdpClient? client;
            lock (_clientLock)
            {
                client = _client;
            }

            if (client == null)
            {
                // The ConnectionStateWorker is responsible for (re)binding the socket
                await Task.Delay(UnboundReceiveWait, TimeProvider, token);
                return;
            }

            try
            {
                var received = await client.ReceiveAsync(token);
                var response = CommandStringFormat == CommandStringFormat.Ascii
                    ? Encoding.Latin1.GetString(received.Buffer)
                    : ConvertByteArrayToString(received.Buffer);
                InvokeResponseHandlers(response, received.Buffer);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SocketException e)
            {
                await HandleDeadSocket(e, token);
            }
            catch (ObjectDisposedException e)
            {
                await HandleDeadSocket(e, token);
            }
            catch (Exception e)
            {
                LogException(e);
                await Task.Delay(ReceiveFailureWait, TimeProvider, token);
            }
        }
    }

    private async Task HandleDeadSocket(Exception e, CancellationToken token)
    {
        LogException(e, "Multicast socket failed, it will be rebound");
        ReportConnectionFailure(e);
        ConnectionState = ConnectionState.Error;
        lock (_clientLock)
        {
            try
            {
                _client?.Dispose();
            }
            catch (Exception disposeException)
            {
                LogException(disposeException);
            }
            _client = null;
        }
        if (_bindBackoffMs == InitialBindBackoffMs)
            _bindBackoffMs *= 2;
        else
            await BindBackoffDelay(token);
        _ = ConnectionStateWorker.Restart();
    }

    private async Task BindBackoffDelay(CancellationToken token)
    {
        // jittered exponential backoff
        var jitter = Random.Shared.Next(-200, 200);
        var delay = Math.Min(_bindBackoffMs + jitter, (int)MaxBackoff.TotalMilliseconds);
        _bindBackoffMs = Math.Min(_bindBackoffMs * 2, (int)MaxBackoff.TotalMilliseconds);
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(500, delay)), TimeProvider, token);
    }

    public override void Send(string message) => Send(Bytes.FromString(message), message);

    public override void Send(byte[] bytes) => Send(bytes, null);

    private void Send(byte[] bytes, string? message)
    {
        using (PushProperties("Send"))
        {
            UdpClient? client;
            IPEndPoint? remoteEndPoint;
            lock (_clientLock)
            {
                client = _client;
                remoteEndPoint = _remoteEndPoint;
            }

            if (client == null)
            {
                LogWarning("Multicast client not bound yet, dropping message");
                return;
            }

            try
            {
                client.Send(bytes, bytes.Length, remoteEndPoint);
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

    protected override async Task ProcessSendQueue(CancellationToken token) => await SendQueueWorker.Stop();

    protected override async Task CheckConnectionState(CancellationToken token)
    {
        using (PushProperties("CheckConnectionState"))
        {
            bool bound;
            lock (_clientLock)
            {
                bound = _client != null;
            }

            if (bound)
            {
                await Task.Delay(BoundCheckInterval, TimeProvider, token);
                _bindBackoffMs = InitialBindBackoffMs;
                return;
            }

            if (!TryBind())
                await BindBackoffDelay(token);
        }
    }

    public override void Connect()
    {
        using (PushProperties("Connect"))
        {
            LogDebug("Connect not supported");
        }
    }

    public override void Reconnect()
    {
        using (PushProperties("Reconnect"))
        {
            LogDebug("Reconnect not supported");
        }
    }

    public override void Disconnect()
    {
        using (PushProperties("Disconnect"))
        {
            LogDebug("Disconnect not supported");
        }
    }
}