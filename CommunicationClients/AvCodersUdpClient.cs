using System.Net;
using Serilog;
using Core_UdpClient = AVCoders.Core.UdpClient;
using UdpClient = System.Net.Sockets.UdpClient;

namespace AVCoders.CommunicationClients;

public class AvCodersUdpClient : Core_UdpClient
{
    private UdpClient? _client;
    private IPEndPoint? _ipEndPoint;
    private readonly Queue<QueuedPayload<byte[]>> _sendQueue = new();

    public AvCodersUdpClient(string ipAddress, ushort port = 0, string name = "") : 
        base(ipAddress, port, name)
    {
        _client = CreateClient();
        
        if (IPAddress.TryParse(Host, out var remoteIpAddress))
            _ipEndPoint = new IPEndPoint(remoteIpAddress, Port);
        
        // This works around a race condition coming from base being called first.
        ReceiveThreadWorker.Restart();
    }

    private UdpClient? CreateClient()
    {
        using (PushProperties("CreateClient"))
        {
            try
            {
                ConnectionState = ConnectionState.Connecting;
                var client = new UdpClient(Host, Port);
                if (IPAddress.TryParse(Host, out var remoteIpAddress))
                    _ipEndPoint = new IPEndPoint(remoteIpAddress, Port);
                ReceiveThreadWorker.Restart();
                ConnectionStateWorker.Restart();
                ConnectionState = ConnectionState.Connected;
                return client;
            }
            catch (Exception e)
            {
                LogException(e);
                ConnectionState = ConnectionState.Disconnected;
            }
        }
        return null;
    }

    protected override async Task Receive(CancellationToken token)
    {
        using (PushProperties("Receive"))
        {
            if (_ipEndPoint == null)
            {
                Log.Debug("Client disconnected, aborting receive");
                await ReceiveThreadWorker.Stop();
                return;
            }

            if (_client is not { Available: > 0 })
            {
                await Task.Delay(1100, token);
                return;
            }

            try
            {
                var received = _client.Receive(ref _ipEndPoint);
                InvokeResponseHandlers(ConvertByteArrayToString(received), received);
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
        if (_client == null)
        {
            Log.Debug("Messages in send queue will not be sent while client is not connected");
            await Task.Delay(500, token);
        }
        else
        {
            while (_sendQueue.Count > 0)
            {
                var item = _sendQueue.Dequeue();
                if (Math.Abs((DateTime.Now - item.Timestamp).TotalSeconds) < QueueTimeout)
                    await _client.SendAsync(item.Payload, token);
            }
            await Task.Delay(1100, token);
        }
    }

    protected override async Task CheckConnectionState(CancellationToken token)
    {
        using (PushProperties("CheckConnectionState"))
        {
            if (ConnectionState is not (ConnectionState.Connected or ConnectionState.Connecting))
            {
                CreateClient();
            }
            await Task.Delay(TimeSpan.FromSeconds(30), token);
        }
    }

    public override void SetPort(ushort port)
    {
        Port = port;
        Reconnect();
    }

    public override void SetHost(string host)
    {
        Host = host;
        Reconnect();
    }

    public override void Connect()
    {
        _sendQueue.Clear();
        Reconnect();
    }

    public override void Reconnect()
    {
        ConnectionState = ConnectionState.Disconnecting;
        _client?.Close();
        ConnectionState = ConnectionState.Disconnected;
        CreateClient();
    }

    public override void Disconnect()
    {
        ConnectionState = ConnectionState.Disconnecting;
        _client = null;
        _ipEndPoint = null;
        ReceiveThreadWorker.Stop();
        ConnectionStateWorker.Stop();
        ConnectionState = ConnectionState.Disconnected;
    }

    public override void Send(byte[] bytes)
    {
        using (PushProperties("Send"))
        {
            try
            {
                if (_client == null)
                {
                    _sendQueue.Enqueue(new QueuedPayload<byte[]>(DateTime.Now, bytes));
                    return;
                }

                _client.Send(bytes, bytes.Length);
                InvokeRequestHandlers(bytes);
            }
            catch (Exception e)
            {
                LogException(e);
            }
        }
    }

    public override void Send(string message)
    {
        Send(Bytes.FromString(message));
        InvokeRequestHandlers(message);
    }
}