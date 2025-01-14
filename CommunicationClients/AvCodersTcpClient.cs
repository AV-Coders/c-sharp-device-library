using System.Net.Sockets;
using System.Text;
using Core_TcpClient = AVCoders.Core.TcpClient;
using TcpClient = System.Net.Sockets.TcpClient;

namespace AVCoders.CommunicationClients;

public class AvCodersTcpClient : Core_TcpClient
{
    private TcpClient _client;
    private readonly Queue<QueuedPayload<byte[]>> _sendQueue = new();

    public AvCodersTcpClient(string host, ushort port = 23, string name = "") :
        base(host, port, name)
    {
        UpdateConnectionState(ConnectionState.Unknown);
        _client = new TcpClient();
        
        ConnectionStateWorker.Restart();
        ReceiveThreadWorker.Restart();
        SendQueueWorker.Restart();
    }

    protected override async Task Receive(CancellationToken token)
    {
        if (!_client.Connected)
        {
            Log("Receive - Client disconnected, waiting 10 seconds");
            await Task.Delay(TimeSpan.FromSeconds(10), token);
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
            catch (IOException e)
            {
                Log($"Receive - IOException:\n{e}", EventLevel.Error);
                Log(e.StackTrace ?? "No Stack Trace available", EventLevel.Error);
                Reconnect();
            }
            catch (ObjectDisposedException e)
            {
                Log($"Receive  - ObjectDisposedException\n{e}", EventLevel.Error);
                Log(e.StackTrace ?? "No Stack Trace available", EventLevel.Error);
                Reconnect();
            }
            catch (Exception e)
            {
                Log($"Receive  - Exception:\n{e}", EventLevel.Error);
                Log(e.StackTrace ?? "No Stack Trace available", EventLevel.Error);
                Reconnect();
            }
            await Task.Delay(TimeSpan.FromMilliseconds(30), token);
        }
    }

    protected override async Task CheckConnectionState(CancellationToken token)
    {
        if (_client.Connected)
        {
            UpdateConnectionState(ConnectionState.Connected);
            await Task.Delay(TimeSpan.FromSeconds(17), token);
        }
        else
        {
            UpdateConnectionState(ConnectionState.Connecting);
            try
            {
                var connectResult = _client.BeginConnect(Host, Port, null, null);
                var success = connectResult.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1));

                if (!success)
                {
                    LogHandlers?.Invoke("1 second connection wait failed, marking as disconnected");
                    UpdateConnectionState(ConnectionState.Disconnected);
                }

                _client.EndConnect(connectResult);
            }
            catch (SocketException e)
            {
                Log($"Check Connection State  - Socket Exception:{e.Message}", EventLevel.Error);
                UpdateConnectionState(ConnectionState.Disconnected);
            }
            catch (Exception e)
            {
                Log($"Check Connection State  - New Exception:{e.Message}", EventLevel.Error);
                Log(e.GetType().ToString(), EventLevel.Error);
                Log(e.StackTrace ?? "No Stack Trace available", EventLevel.Error);
                UpdateConnectionState(ConnectionState.Disconnected);
            }
            await Task.Delay(TimeSpan.FromSeconds(5), token);
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
        if (_client.Connected)
        {
            try
            {
                _client.GetStream().Write(bytes);
                InvokeRequestHandlers(bytes);
            }
            catch (IOException e)
            {
                Log($"IOException while sending, Queueing message: {e.Message}\r\n{e.StackTrace ?? "No Stack trace available"}", EventLevel.Error);
                _sendQueue.Enqueue(new QueuedPayload<byte[]>(DateTime.Now, bytes));
            }
        }
        else
            _sendQueue.Enqueue(new QueuedPayload<byte[]>(DateTime.Now, bytes));
    }

    public override void SetPort(ushort port)
    {
        Log($"Setting port to {port}");
        Port = port;
        Reconnect();
    }

    public override void SetHost(string host)
    {
        Log($"Setting host to {host}");
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
        Log($"Reconnecting");
        UpdateConnectionState(ConnectionState.Disconnecting);
        _client.Close();
        _client = new TcpClient();
        UpdateConnectionState(ConnectionState.Disconnected);
        // The worker will handle reconnection
    }

    public override void Disconnect()
    {
        Log($"Disconnecting");
        ConnectionStateWorker.Stop();
        UpdateConnectionState(ConnectionState.Disconnecting);
        _client.Close();
        _client = new TcpClient();
        UpdateConnectionState(ConnectionState.Disconnected);
    }
}