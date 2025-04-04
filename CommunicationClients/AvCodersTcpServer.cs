﻿using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Serilog.Context;
using Core_TcpClient = AVCoders.Core.TcpClient;
using TcpClient = System.Net.Sockets.TcpClient;

namespace AVCoders.CommunicationClients;

public class AvCodersTcpServer : Core_TcpClient
{
    private TcpListener _server;
    private readonly ConcurrentBag<TcpClient> _clients = new();

    public AvCodersTcpServer(ushort port, string name = "") : base("Any", port, name)
    {
        _server = new TcpListener(IPAddress.Any, port);
        _server.Start();
        
        ReceiveThreadWorker.Restart(); // Used to connect to new clients
        ConnectionStateWorker.Restart();
    }

    public override void Send(string message) => Send(Bytes.FromString(message));

    public override void Send(byte[] bytes)
    {
        using (LogContext.PushProperty(MethodProperty, "Send"))
        {
            foreach (TcpClient client in _clients)
            {
                try
                {
                    if (client.Connected)
                        client.GetStream().Write(bytes);
                }
                catch (IOException e)
                {
                    LogException(e);
                }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (LogContext.PushProperty(MethodProperty, "HandleClientAsync"))
        using (client)
        {
            try
            {
                await using NetworkStream stream = client.GetStream();
                byte[] buffer = new byte[1024];
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token)) != 0)
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

    protected override Task ProcessSendQueue(CancellationToken token) => SendQueueWorker.Stop();

    protected override async Task Receive(CancellationToken token)
    {
        using (LogContext.PushProperty(MethodProperty, "Receive"))
        {
            TcpClient client = await _server.AcceptTcpClientAsync(token);
            _clients.Add(client);
            IPEndPoint? remoteIpEndPoint = client.Client.RemoteEndPoint as IPEndPoint ?? default;
            Debug($"Added client - {remoteIpEndPoint?.Address}");
            _ = HandleClientAsync(client, token);
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
    }

    protected override async Task CheckConnectionState(CancellationToken token)
    {
        using (LogContext.PushProperty(MethodProperty, "CheckConnectionState"))
        {
            Debug($"Checking client status for {_clients.Count} clients");
            foreach (TcpClient client in _clients)
            {
                if (client.Connected)
                    continue;

                Debug("Removing a client");
                _clients.TryTake(out _);
            }

            ConnectionState = _clients.IsEmpty ? ConnectionState.Disconnected : ConnectionState.Connected;
            await Task.Delay(TimeSpan.FromSeconds(45), token);
        }
    }

    public override void SetHost(string host) => Error("Set Host is not supported");

    public override void SetPort(ushort port)
    {
        ReceiveThreadWorker.Stop();
        Port = port;
        _server = new TcpListener(IPAddress.Any, Port);
        ReceiveThreadWorker.Restart();
    }

    public override void Connect() => ReceiveThreadWorker.Restart();

    public override void Reconnect() => ReceiveThreadWorker.Restart();

    public override void Disconnect() => ReceiveThreadWorker.Stop();
}