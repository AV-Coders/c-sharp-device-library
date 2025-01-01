using System.Net;
using System.Text;
using UdpClient = System.Net.Sockets.UdpClient;
namespace AVCoders.CommunicationClients;

public class AvCodersMulticastClient : IpComms
{
    private readonly UdpClient _client;
    private readonly IPEndPoint _remoteEndPoint;
    private readonly Queue<QueuedPayload<Byte[]>> _sendQueue = new();

    public AvCodersMulticastClient(string ipAddress, ushort port, string name) : base(ipAddress, port, name)
    {
        UpdateConnectionState(ConnectionState.Connecting);
        IPEndPoint localEndPoint = new IPEndPoint(IPAddress.Any, port);
        _client = new UdpClient(localEndPoint);

        _remoteEndPoint = new IPEndPoint(IPAddress.Parse(ipAddress), port);
        _client.JoinMulticastGroup(IPAddress.Parse(ipAddress));

        UpdateConnectionState(ConnectionState.Connected);

        // This works around a race condition coming from base being called first.
        ReceiveThreadWorker.Restart();
    }

    protected override async Task Receive(CancellationToken token)
    {
        try
        {
            Log("Receiving");
            var received = await _client.ReceiveAsync(token);
            InvokeResponseHandlers(Encoding.UTF8.GetString(received.Buffer), received.Buffer);
        }
        catch (Exception e)
        {
            Log($"Receive - Error: {e.Message}");
        }
    }

    public override void Send(byte[] bytes)
    {
        try
        {
            _client.Send(bytes, bytes.Length, _remoteEndPoint);
        }
        catch (Exception e)
        {
            Log($"Send - Error: {e.Message}\r\n {e.StackTrace}");
        }
    }

    protected override async Task ProcessSendQueue(CancellationToken token) => await SendQueueWorker.Stop();

    protected override async Task CheckConnectionState(CancellationToken token) => await ConnectionStateWorker.Stop();

    public override void SetPort(ushort port) => Log("Method not supported");

    public override void SetHost(string host) => Log("Method not supported");

    public override void Connect() => Log("Method not supported");

    public override void Reconnect() => Log("Method not supported");

    public override void Disconnect() => Log("Method not supported");

    public override void Send(String message) => Send(ConvertStringToByteArray(message));
}