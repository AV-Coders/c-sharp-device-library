using System.Net.Sockets;
using System.Text;
using Serilog;
using Serilog.Context;
using Core_TcpClient = AVCoders.Core.TcpClient;
using TcpClient = System.Net.Sockets.TcpClient;

namespace AVCoders.CommunicationClients;

public class AvCodersTcpClient : Core_TcpClient
{
    private TcpClient _client;
    private readonly Queue<QueuedPayload<byte[]>> _sendQueue = new();

    public AvCodersTcpClient(string host, ushort port, string name, CommandStringFormat commandStringFormat) :
        base(host, port, name, commandStringFormat)
    {
        ConnectionState = ConnectionState.Unknown;
        _client = new TcpClient();
        
        ConnectionStateWorker.Restart();
        ReceiveThreadWorker.Restart();
        SendQueueWorker.Restart();
    }

    protected override async Task Receive(CancellationToken token)
    {
        using (LogContext.PushProperty(MethodProperty, "Receive"))
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
        using (LogContext.PushProperty(MethodProperty, "Send"))
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
        ConnectionStateWorker.Restart();
    }

    public override void Reconnect()
    {
        ConnectionState = ConnectionState.Disconnecting;
        _client.Close();
        _client = new TcpClient();
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
}