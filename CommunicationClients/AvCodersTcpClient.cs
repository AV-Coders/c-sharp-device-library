using System.Net.Sockets;
using System.Text;
using Serilog.Context;
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
        using (LogContext.PushProperty(MethodProperty, "Receive"))
        {
            if (!_client.Connected)
            {
                Debug("Client disconnected, waiting 10 seconds");
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
                    Error("IOException:");
                    Error(e.Message);
                    Error(e.StackTrace ?? "No Stack Trace available");
                    Reconnect();
                }
                catch (ObjectDisposedException e)
                {
                    Error("ObjectDisposedException");
                    Error(e.Message);
                    Error(e.StackTrace ?? "No Stack Trace available");
                    Reconnect();
                }
                catch (Exception e)
                {
                    Error(e.GetType().Name);
                    Error(e.Message);
                    Error(e.StackTrace ?? "No Stack Trace available");
                    Reconnect();
                }
                await Task.Delay(TimeSpan.FromMilliseconds(30), token);
            }
        }
    }

    protected override async Task CheckConnectionState(CancellationToken token)
    {
        using (LogContext.PushProperty(MethodProperty, "CheckConnectionState"))
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
                        Info("1 second connection wait failed, marking as disconnected");
                        UpdateConnectionState(ConnectionState.Disconnected);
                    }

                    _client.EndConnect(connectResult);
                }
                catch (SocketException e)
                {
                    Error($"Check Connection State  - Socket Exception: {e.Message}");
                    UpdateConnectionState(ConnectionState.Disconnected);
                }
                catch (IOException e)
                {
                    Error($"Check Connection State - IOException: {e.Message}");
                    Error(e.StackTrace ?? "No Stack Trace available");
                    _client.Close();
                    _client = new TcpClient();
                    UpdateConnectionState(ConnectionState.Disconnected);
                }
                catch (Exception e)
                {
                    Error($"Check Connection State  - New Exception: {e.Message}");
                    Error(e.GetType().ToString());
                    Error(e.StackTrace ?? "No Stack Trace available");
                    UpdateConnectionState(ConnectionState.Disconnected);
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
                    Error(
                        $"IOException while sending, Queueing message: {e.Message}\r\n{e.StackTrace ?? "No Stack trace available"}");
                    _sendQueue.Enqueue(new QueuedPayload<byte[]>(DateTime.Now, bytes));
                }
            }
            else
                _sendQueue.Enqueue(new QueuedPayload<byte[]>(DateTime.Now, bytes));
        }
    }

    public override void SetPort(ushort port)
    {
        Debug($"Setting port to {port}");
        Port = port;
        Reconnect();
    }

    public override void SetHost(string host)
    {
        Debug($"Setting host to {host}");
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
        Debug($"Reconnecting");
        UpdateConnectionState(ConnectionState.Disconnecting);
        _client.Close();
        _client = new TcpClient();
        UpdateConnectionState(ConnectionState.Disconnected);
        // The worker will handle reconnection
    }

    public override void Disconnect()
    {
        Debug($"Disconnecting");
        ConnectionStateWorker.Stop();
        UpdateConnectionState(ConnectionState.Disconnecting);
        _client.Close();
        _client = new TcpClient();
        UpdateConnectionState(ConnectionState.Disconnected);
    }
}