using System.Text;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Protocol;
using MqttClient = AVCoders.Core.MqttClient;

namespace AVCoders.CommunicationClients;

public class AvCodersMqttClient : MqttClient
{
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(3);
    private readonly MqttClientOptions _mqttClientOptions;
    private readonly IMqttClient _mqttClient;
    private readonly ThreadWorker _connectionWorker;
    private readonly Dictionary<string, List<Action<string>>> _handlers = new();
    private readonly object _handlersLock = new();

    /// <summary>
    /// Creates an MQTT client. Pass a null or empty <paramref name="username"/> to connect
    /// anonymously - the CONNECT packet then omits the username/password flags entirely,
    /// which is what brokers expect for anonymous sessions. A password without a username
    /// is invalid in MQTT and is ignored.
    /// </summary>
    public AvCodersMqttClient(string host, ushort port, string? username, string? password, string name):
        base(host, port, name)
    {
        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(Host, Port);
        if (!string.IsNullOrEmpty(username))
            optionsBuilder = optionsBuilder.WithCredentials(username, password);
        _mqttClientOptions = optionsBuilder.Build();
        _mqttClient = new MqttClientFactory().CreateMqttClient();
        _mqttClient.ConnectedAsync += RegisterDevicesToMqttServer;
        _mqttClient.DisconnectedAsync += HandleMqttDisconnection;
        _mqttClient.ApplicationMessageReceivedAsync += HandleMqttMessage;
        _connectionWorker = new ThreadWorker(ConnectToBroker, ReconnectInterval);
        _connectionWorker.Restart();
    }

    private async Task ConnectToBroker(CancellationToken token)
    {
        if (_mqttClient.IsConnected)
        {
            ConnectionState = ConnectionState.Connected;
            return;
        }
        ConnectionState = ConnectionState.Connecting;
        try
        {
            var result = await _mqttClient.ConnectAsync(_mqttClientOptions, CancellationToken.None);
            if (result.ResultCode == MqttClientConnectResultCode.Success)
                return;
            ReportConnectionFailure($"{Host}:{Port} rejected the connection: {result.ResultCode}");
            ConnectionState = ConnectionState.Disconnected;
        }
        catch (Exception e)
        {
            using (PushProperties())
                LogException(e, "MQTT connection attempt failed");
            ReportConnectionFailure(DescribeConnectionError(e));
            ConnectionState = ConnectionState.Disconnected;
        }
    }

    private Task HandleMqttMessage(MqttApplicationMessageReceivedEventArgs arg)
    {
        using var trigger = Trigger.Begin();
        string topic = arg.ApplicationMessage.Topic;
        var rawPayload = Encoding.UTF8.GetString(arg.ApplicationMessage.Payload);
        lock (_handlersLock)
        {
            if(_handlers.TryGetValue(topic, out var handlers))
                foreach (var handler in handlers)
                    handler(rawPayload);
        }

        InvokeResponseHandlers($"{topic} - {rawPayload}");
        return Task.CompletedTask;
    }

    public override void SubscribeToTopic(string topic, Action<string> handler)
    {
        lock (_handlersLock)
        {
            if (!_handlers.ContainsKey(topic))
            {
                _handlers.Add(topic, [handler]);
                _ = Subscribe(topic);
            }
            else
                _handlers[topic].Add(handler);
        }
    }

    private async Task Subscribe(string topic)
    {
        try
        {
            var result = await _mqttClient.SubscribeAsync(topic, MqttQualityOfServiceLevel.AtLeastOnce);
            foreach (var refused in result.Items.Where(item => item.ResultCode > MqttClientSubscribeResultCode.GrantedQoS2))
            {
                using (PushProperties())
                    LogWarning("The broker refused the subscription to {Topic}: {ResultCode}", topic, refused.ResultCode);
            }
        }
        catch (MqttClientNotConnectedException)
        {
            using (PushProperties())
                LogVerbose("Not subscribing to {Topic} yet, the client is not connected", topic);
        }
        catch (Exception e)
        {
            using (PushProperties())
                LogException(e, $"Subscribing to {topic} failed");
        }
    }

    private Task HandleMqttDisconnection(MqttClientDisconnectedEventArgs arg)
    {
        ConnectionState = ConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    private Task RegisterDevicesToMqttServer(MqttClientConnectedEventArgs arg)
    {
        ConnectionState = ConnectionState.Connected;
        List<string> topics;
        lock (_handlersLock)
        {
            topics = _handlers.Keys.ToList();
        }
        foreach (var topic in topics)
            _ = Subscribe(topic);
        return Task.CompletedTask;
    }

    public override void Send(string topic, string payload)
    {
        MqttApplicationMessage message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .Build();
        _ = PublishMqttMessage(message, $"{topic} - {payload}");
    }

    private async Task PublishMqttMessage(MqttApplicationMessage message, string request)
    {
        try
        {
            await _mqttClient.PublishAsync(message);
        }
        catch (MqttClientNotConnectedException)
        {
            using (PushProperties())
                LogVerbose("Not publishing {Request}, the client is not connected", request);
            return;
        }
        catch (Exception e)
        {
            using (PushProperties())
                LogException(e, $"Publishing {request} failed");
            return;
        }
        InvokeRequestHandlers(request);
    }

    /// <summary>
    /// Deprecated: Use Send(MqttApplicationMessage) instead.
    /// </summary>
    public override void Send(string message)
    {
        throw new InvalidOperationException("Use the Send(MqttApplicationMessage) method instead.");
    }

    /// <summary>
    /// Deprecated: Use Send(MqttApplicationMessage) instead.
    /// </summary>
    public override void Send(byte[] bytes)
    {
        throw new InvalidOperationException("Use the Send(MqttApplicationMessage) method instead.");
    }
}