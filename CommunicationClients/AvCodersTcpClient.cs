using System.Net.Sockets;
using System.Text;
using Serilog;
using Core_TcpClient = AVCoders.Core.TcpClient;
using TcpClient = System.Net.Sockets.TcpClient;

namespace AVCoders.CommunicationClients;

public class AvCodersTcpClient : Core_TcpClient
{
    private readonly bool _useKeepAlive;
    private TcpClient _client;
    private readonly Queue<QueuedPayload<byte[]>> _sendQueue = new();

    public AvCodersTcpClient(string host, ushort port, string name, CommandStringFormat commandStringFormat, bool useKeepAlive = false) :
        base(host, port, name, commandStringFormat)
    {
        _useKeepAlive = useKeepAlive;
        ConnectionState = ConnectionState.Unknown;
        _client = new TcpClient();
        ConfigureKeepAlive(_client);
        ConnectionStateWorker.Restart();
        ReceiveThreadWorker.Restart();
        SendQueueWorker.Restart();
    }

    protected override async Task Receive(CancellationToken token)
    {
        using (PushProperties("Receive"))
        {
            if (!_client.Connected)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
            }
            else
            {
                try
                {
                    byte[] buffer = new byte[1024];
                    var bytesRead = await _client.GetStream().ReadAsync(buffer, token);

                    if (bytesRead > 0)
                    {
                        string response = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                        InvokeResponseHandlers(response, buffer.Take(bytesRead).ToArray());
                    }
                }
                catch (Exception e)
                {
                    LogException(e);
                    Reconnect();
                }
            }
        }
    }

    protected override async Task CheckConnectionState(CancellationToken token)
    {
        using (PushProperties("CheckConnectionState"))
        {
            if (_client.Connected)
            {
                ConnectionState = ConnectionState.Connected;
                await Task.Delay(TimeSpan.FromSeconds(17), token);
            }
            else
            {
                ConnectionState = ConnectionState.Connecting;
                try
                {
                    var connectResult = _client.BeginConnect(Host, Port, null, null);
                    var success = connectResult.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1));

                    if (!success)
                    {
                        Log.Error("1 second connection wait failed, marking as disconnected");
                        ConnectionState = ConnectionState.Disconnected;
                    }

                    _client.EndConnect(connectResult);
                    ConfigureKeepAlive(_client);

                    ConnectionState = ConnectionState.Connected;
                    ReceiveThreadWorker.Restart();
                }
                catch (SocketException e)
                {
                    LogException(e);
                    ConnectionState = ConnectionState.Disconnected;
                }
                catch (IOException e)
                {
                    LogException(e);
                    _client.Close();
                    _client = new TcpClient();
                    ConfigureKeepAlive(_client);
                    ConnectionState = ConnectionState.Disconnected;
                }
                catch (Exception e)
                {
                    LogException(e);
                    ConnectionState = ConnectionState.Disconnected;
                }

                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
        }
    }
    

    protected override async Task ProcessSendQueue(CancellationToken token)
    {
        if (!_client.Connected)
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        else
        {
            while (_sendQueue.Count > 0)
            {
                var item = _sendQueue.Dequeue();
                if (Math.Abs((DateTime.Now - item.Timestamp).TotalSeconds) < QueueTimeout)
                    await _client.GetStream().WriteAsync(item.Payload, token);
            }
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
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
            if (_client.Connected)
            {
                try
                {
                    _client.GetStream().Write(bytes);
                    InvokeRequestHandlers(bytes);
                }
                catch (IOException e)
                {
                    LogException(e);
                    _sendQueue.Enqueue(new QueuedPayload<byte[]>(DateTime.Now, bytes));
                    Reconnect();
                }
            }
            else
                _sendQueue.Enqueue(new QueuedPayload<byte[]>(DateTime.Now, bytes));
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
        _client.Close();
        _client = new TcpClient();
        ConfigureKeepAlive(_client);
        ConnectionState = ConnectionState.Disconnected;
        // The worker will handle reconnection
    }
    

    public override void Disconnect()
    {
        ConnectionStateWorker.Stop();
        ConnectionState = ConnectionState.Disconnecting;
        _client.Close();
        _client = new TcpClient();
        ConnectionState = ConnectionState.Disconnected;
    }

    private void ConfigureKeepAlive(TcpClient client)
    {
        if (!_useKeepAlive)
            return;
        try
        {
            Socket socket = client.Client;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 3);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (SocketException e)
        {
            // On some platforms one or more options may not be supported; ignore and proceed with what worked.
            LogException(e, "There was an error setting keepalive options");
            
        }
        catch (PlatformNotSupportedException e)
        {
            // Ignore on platforms that don't support tuning keepalive values.
            LogException(e, "This platform doesn't support keepalive options");
        }
    }
}