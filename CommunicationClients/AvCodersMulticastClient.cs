using System.Net;
using System.Text;
using Serilog;
using Serilog.Context;
using UdpClient = System.Net.Sockets.UdpClient;

namespace AVCoders.CommunicationClients;

public class AvCodersMulticastClient : IpComms
{
    private readonly UdpClient _client;
    private readonly IPEndPoint _remoteEndPoint;
    private readonly Queue<QueuedPayload<Byte[]>> _sendQueue = new();

    public AvCodersMulticastClient(string ipAddress, ushort port, string name) : base(ipAddress, port, name)
    {
        using (LogContext.PushProperty(MethodProperty, "Constructor"))
        {
            ConnectionState = ConnectionState.Connecting;
            IPEndPoint localEndPoint = new IPEndPoint(IPAddress.Any, 0);
            _client = new UdpClient(localEndPoint);

            var multicastIp = IPAddress.Parse(ipAddress);
            _remoteEndPoint = new IPEndPoint(multicastIp, port);
            _client.JoinMulticastGroup(multicastIp);

            ConnectionState = ConnectionState.Connected;

            // This works around a race condition coming from base being called first.
            ReceiveThreadWorker.Restart();
        }
    }

    protected override async Task Receive(CancellationToken token)
    {
        using (LogContext.PushProperty(MethodProperty, "Receive"))
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
        using (LogContext.PushProperty(MethodProperty, "Send"))
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

    public override void SetPort(ushort port)
    {
        using (PushProperties("SetPort"))
        {
            Log.Debug("Set Port not supported");
        }
    }

    public override void SetHost(string host)
    {
        using (PushProperties("SetHost"))
        {
            Log.Debug("Set Host not supported");
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

    public override void Send(String message)
    {
        Send(Bytes.FromString(message));
        InvokeRequestHandlers(message);
    }
}