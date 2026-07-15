using System.Text;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Protocol;
using MqttClient = AVCoders.Core.MqttClient;

namespace AVCoders.CommunicationClients;

public class AvCodersMqttClient : MqttClient
{
    private readonly MqttClientOptions _mqttClientOptions;
    private readonly IMqttClient _mqttClient;
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
        ConnectionState = ConnectionState.Connecting;
        _ = ConnectInitialAsync();
    }

    private async Task ConnectInitialAsync()
    {
        try
        {
            await _mqttClient.ConnectAsync(_mqttClientOptions, CancellationToken.None);
        }
        catch (Exception e)
        {
            LogException(e, "Initial MQTT connection failed");
            ConnectionState = ConnectionState.Disconnected;
        }
    }
    private Task HandleMqttMessage(MqttApplicationMessageReceivedEventArgs arg)
    {
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
                _mqttClient.SubscribeAsync(topic, MqttQualityOfServiceLevel.AtLeastOnce);
            }
            else
                _handlers[topic].Add(handler);
        }
    }

    private async Task HandleMqttDisconnection(MqttClientDisconnectedEventArgs arg)
    {
        while (!_mqttClient.IsConnected)
        {
            ConnectionState = ConnectionState.Disconnected;
            await Task.Delay(TimeSpan.FromSeconds(3));
            ConnectionState = ConnectionState.Connecting;
            LogDebug("Reconnecting to MQTT server");
            await _mqttClient.ConnectAsync(_mqttClientOptions, CancellationToken.None);
        }
    }

    private Task RegisterDevicesToMqttServer(MqttClientConnectedEventArgs arg)
    {
        ConnectionState = ConnectionState.Connected;
        List<string> topics;
        lock (_handlersLock)
        {
            topics = _handlers.Keys.ToList();
        }
        topics.ForEach(topic => _mqttClient.SubscribeAsync(topic, MqttQualityOfServiceLevel.AtLeastOnce));
        return Task.CompletedTask;
    }

    public override void Send(string topic, string payload)
    {
        MqttApplicationMessage message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .Build();
        PublishMqttMessage(message);
        InvokeRequestHandlers($"{topic} - {payload}");
    }

    private void PublishMqttMessage(MqttApplicationMessage message)
    {
        using (PushProperties())
        {
            try
            {
                _mqttClient.PublishAsync(message);
            }
            catch (MqttClientNotConnectedException e)
            {
                LogVerbose("MqttClientNotConnectedException: {Exception}", e);
                _ = HandleMqttDisconnection(new MqttClientDisconnectedEventArgs(true, new MqttClientConnectResult(),
                    MqttClientDisconnectReason.UnspecifiedError, String.Empty, [], null));
            }
            catch (Exception e)
            {
                LogException(e);
            }
        }
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