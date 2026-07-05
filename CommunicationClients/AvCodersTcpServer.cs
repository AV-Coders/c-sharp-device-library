using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Serilog;
using Core_TcpClient = AVCoders.Core.TcpClient;
using TcpClient = System.Net.Sockets.TcpClient;

namespace AVCoders.CommunicationClients;

public class AvCodersTcpServer : Core_TcpClient
{
    private TcpListener? _server;
    private volatile bool _listening;
    private int _bindBackoffMs = 1000; // starts at 1 second
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(20);
    private readonly ConcurrentDictionary<Guid, TcpClient> _clients = new();

    public AvCodersTcpServer(ushort port, string name, CommandStringFormat commandStringFormat)
        : base("Any", port, name, commandStringFormat)
    {
        ConnectionState = ConnectionState.Connecting;

        ReceiveThreadWorker.Restart(); // Used to connect to new clients
        ConnectionStateWorker.Restart(); // Binds the listener, then monitors clients
    }

    private bool TryStartListener()
    {
        using (PushProperties("TryStartListener"))
        {
            TcpListener listener = new TcpListener(IPAddress.Any, Port);
            try
            {
                listener.ExclusiveAddressUse = false;
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Start();

                _server = listener;
                _listening = true;
                _bindBackoffMs = 1000;
                return true;
            }
            catch (Exception e)
            {
                LogException(e, $"Failed to bind TCP server to port {Port}");
                try
                {
                    listener.Server.Dispose();
                }
                catch (Exception disposeException)
                {
                    LogException(disposeException);
                }
                return false;
            }
        }
    }

    private async Task BindBackoffDelay(CancellationToken token)
    {
        // jittered exponential backoff
        var jitter = Random.Shared.Next(-200, 200);
        var delay = Math.Min(_bindBackoffMs + jitter, (int)MaxBackoff.TotalMilliseconds);
        _bindBackoffMs = Math.Min(_bindBackoffMs * 2, (int)MaxBackoff.TotalMilliseconds);
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(500, delay)), token);
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
            foreach (var kvp in _clients)
            {
                TcpClient client = kvp.Value;
                try
                {
                    if (client.Connected && client.GetStream().CanWrite)
                        client.GetStream().Write(bytes);
                }
                catch (IOException e)
                {
                    LogException(e);
                }
                catch (ObjectDisposedException e)
                {
                    LogException(e);
                }
            }
            InvokeRequestHandlers(bytes);
        }
    }

    private async Task HandleClientAsync(TcpClient client, Guid clientId, CancellationToken token)
    {
        using (PushProperties("HandleClientAsync"))
        {
            try
            {
                await using NetworkStream stream = client.GetStream();
                byte[] buffer = new byte[1024];
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token)) != 0)
                {
                    // Ignore probe bytes sent by TCP Client
                    if (bytesRead == 1 && buffer[0] == 0)
                        continue;
                    string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    InvokeResponseHandlers(response, buffer.Take(bytesRead).ToArray());
                }
            }
            catch (Exception e)
            {
                LogException(e);
            }
            finally
            {
                // Clean up this specific client when HandleClientAsync exits
                _clients.TryRemove(clientId, out _);
                client.Dispose();
                Log.Debug("Client {ClientId} disconnected and removed", clientId);
            }
        }
    }

    protected override Task ProcessSendQueue(CancellationToken token) => SendQueueWorker.Stop();

    protected override async Task Receive(CancellationToken token)
    {
        using (PushProperties("Receive"))
        {
            TcpListener? server = _server;
            if (!_listening || server == null)
            {
                // The ConnectionStateWorker is responsible for (re)binding the listener
                await Task.Delay(TimeSpan.FromSeconds(1), token);
                return;
            }

            try
            {
                TcpClient client = await server.AcceptTcpClientAsync(token);
                Guid clientId = Guid.NewGuid();
                _clients.TryAdd(clientId, client);
                IPEndPoint? remoteIpEndPoint = client.Client.RemoteEndPoint as IPEndPoint ?? null;
                Log.Debug("Added client {ClientId} - {IpAddress}", clientId, remoteIpEndPoint?.Address);
                ConnectionState = ConnectionState.Connected;
                _ = HandleClientAsync(client, clientId, token);
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
            catch (SocketException e)
            {
                HandleDeadListener(e);
            }
            catch (ObjectDisposedException e)
            {
                HandleDeadListener(e);
            }
            catch (InvalidOperationException e)
            {
                HandleDeadListener(e);
            }
        }
    }

    private void HandleDeadListener(Exception e)
    {
        LogException(e, "Listener failed, it will be rebound");
        _listening = false;
        try
        {
            _server?.Server.Dispose();
        }
        catch (Exception disposeException)
        {
            LogException(disposeException);
        }
        _server = null;
        ConnectionState = _clients.IsEmpty ? ConnectionState.Error : ConnectionState.Degraded;
    }

    protected override async Task CheckConnectionState(CancellationToken token)
    {
        using (PushProperties("CheckConnectionState"))
        {
            var disconnectedClients = _clients.Where(kvp => !kvp.Value.Connected).ToList();
            foreach (var kvp in disconnectedClients)
            {
                if (_clients.TryRemove(kvp.Key, out var client))
                {
                    Log.Debug("Removing disconnected client {ClientId}", kvp.Key);
                    client.Dispose();
                }
            }

            if (!_listening)
            {
                if (!TryStartListener())
                {
                    ConnectionState = _clients.IsEmpty ? ConnectionState.Error : ConnectionState.Degraded;
                    await BindBackoffDelay(token);
                    return;
                }
            }

            ConnectionState = _clients.IsEmpty ? ConnectionState.Disconnected : ConnectionState.Connected;
            await Task.Delay(TimeSpan.FromSeconds(45), token);
        }
    }

    public override void Connect() => ReceiveThreadWorker.Restart();

    public override void Reconnect() => ReceiveThreadWorker.Restart();

    public override void Disconnect() => ReceiveThreadWorker.Stop();
}