using System.Net;
using System.Net.Sockets;
using System.Text;
using Serilog;
using Serilog.Context;
using UdpClient = System.Net.Sockets.UdpClient;

namespace AVCoders.CommunicationClients;

public class AvCodersMulticastClient : IMulticastClient
{
    private readonly object _clientLock = new();
    private UdpClient? _client;
    private readonly IPAddress _multicastIp;
    private readonly int _ttl;
    private readonly IPEndPoint _remoteEndPoint;
    private int _bindBackoffMs = 1000; // starts at 1 second
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(20);

    public AvCodersMulticastClient(string ipAddress, ushort port, string name, int ttl = 1, CommandStringFormat commandStringFormat = CommandStringFormat.Ascii)
        : base(ipAddress, port, name, commandStringFormat)
    {
        using (PushProperties("Constructor"))
        {
            ConnectionState = ConnectionState.Connecting;

            _multicastIp = IPAddress.Parse(ipAddress);
            _ttl = ttl;
            _remoteEndPoint = new IPEndPoint(_multicastIp, port);

            ConnectionStateWorker.Restart(); // Binds the socket, retrying until it succeeds
            // This works around a race condition coming from base being called first.
            ReceiveThreadWorker.Restart();
        }
    }

    private bool TryBind()
    {
        using (PushProperties("TryBind"))
        {
            var af = _multicastIp.AddressFamily;
            UdpClient client = new UdpClient(af);
            try
            {
                if (af == AddressFamily.InterNetwork)
                {
                    client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, _ttl);
                }
                else if (af == AddressFamily.InterNetworkV6)
                {
                    client.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.HopLimit, _ttl);
                }

                client.Client.ExclusiveAddressUse = false;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                client.Client.Bind(new IPEndPoint(IPAddress.Any, Port));

                client.JoinMulticastGroup(_multicastIp);

                lock (_clientLock)
                {
                    _client = client;
                }
                _bindBackoffMs = 1000;
                ConnectionState = ConnectionState.Connected;
                return true;
            }
            catch (Exception e)
            {
                LogException(e, $"Failed to bind multicast client on port {Port}");
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
                await Task.Delay(TimeSpan.FromSeconds(1), token);
                return;
            }

            try
            {
                var received = await client.ReceiveAsync(token);
                InvokeResponseHandlers(Encoding.UTF8.GetString(received.Buffer), received.Buffer);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SocketException e)
            {
                HandleDeadSocket(e);
            }
            catch (ObjectDisposedException e)
            {
                HandleDeadSocket(e);
            }
            catch (Exception e)
            {
                LogException(e);
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
        }
    }

    private void HandleDeadSocket(Exception e)
    {
        LogException(e, "Multicast socket failed, it will be rebound");
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
        ConnectionState = ConnectionState.Error;
    }

    public override void Send(byte[] bytes)
    {
        using (PushProperties("Send"))
        {
            UdpClient? client;
            lock (_clientLock)
            {
                client = _client;
            }

            if (client == null)
            {
                Log.Warning("Multicast client not bound yet, dropping message");
                return;
            }

            try
            {
                client.Send(bytes, bytes.Length, _remoteEndPoint);
                InvokeRequestHandlers(bytes);
            }
            catch (Exception e)
            {
                LogException(e);
            }
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

            if (!bound)
            {
                if (!TryBind())
                {
                    // jittered exponential backoff
                    var jitter = Random.Shared.Next(-200, 200);
                    var delay = Math.Min(_bindBackoffMs + jitter, (int)MaxBackoff.TotalMilliseconds);
                    _bindBackoffMs = Math.Min(_bindBackoffMs * 2, (int)MaxBackoff.TotalMilliseconds);
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(500, delay)), token);
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(30), token);
        }
    }

    public override void Connect()
    {
        using (PushProperties("Connect"))
        {
            Log.Debug("Connect not supported");
        }
    }

    public override void Reconnect()
    {
        using (PushProperties("Reconnect"))
        {
            Log.Debug("Reconnect not supported");
        }
    }

    public override void Disconnect()
    {
        using (PushProperties("Disconnect"))
        {
            Log.Debug("Disconnect not supported");
        }
    }

    public override void Send(string message)
    {
        Send(Bytes.FromString(message));
        InvokeRequestHandlers(message);
    }
}