using System.Net;
using System.Net.Sockets;
using System.Text;
using Serilog;
using Serilog.Context;
using UdpClient = System.Net.Sockets.UdpClient;

namespace AVCoders.CommunicationClients;

public class AvCodersMulticastClient : IMulticastClient
{
    private readonly UdpClient _client;
    private readonly IPEndPoint _remoteEndPoint;

    public AvCodersMulticastClient(string ipAddress, ushort port, string name, int ttl = 1, CommandStringFormat commandStringFormat = CommandStringFormat.Ascii)
        : base(ipAddress, port, name, commandStringFormat)
    {
        using (PushProperties("Constructor"))
        {
            ConnectionState = ConnectionState.Connecting;

            var multicastIp = IPAddress.Parse(ipAddress);
            var af = multicastIp.AddressFamily;
            _client = new UdpClient(af);

            if (af == AddressFamily.InterNetwork)
            {
                _client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, ttl);
            }
            else if (af == AddressFamily.InterNetworkV6)
            {
                _client.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.HopLimit, ttl);
            }

            _client.Client.ExclusiveAddressUse = false;
            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _client.Client.Bind(new IPEndPoint(IPAddress.Any, port));

            _remoteEndPoint = new IPEndPoint(multicastIp, port);
            _client.JoinMulticastGroup(multicastIp);

            ConnectionState = ConnectionState.Connected;

            // This works around a race condition coming from base being called first.
            ReceiveThreadWorker.Restart();
        }
    }

    protected override async Task Receive(CancellationToken token)
    {
        using (PushProperties("Receive"))
        {
            try
            {
                var received = await _client.ReceiveAsync(token);
                InvokeResponseHandlers(Encoding.UTF8.GetString(received.Buffer), received.Buffer);
            }
            catch (Exception e)
            {
                LogException(e);
            }
        }
    }

    public override void Send(byte[] bytes)
    {
        using (PushProperties("Send"))
        {
            try
            {
                _client.Send(bytes, bytes.Length, _remoteEndPoint);
                InvokeRequestHandlers(bytes);
            }
            catch (Exception e)
            {
                LogException(e);
            }
        }
    }

    protected override async Task ProcessSendQueue(CancellationToken token) => await SendQueueWorker.Stop();
    protected override async Task CheckConnectionState(CancellationToken token) => await ConnectionStateWorker.Stop();

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