using System.Diagnostics.Tracing;
using System.Text;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Protocol;
using Serilog;
using MqttClient = AVCoders.Core.MqttClient;

namespace AVCoders.CommunicationClients;

public class AvCodersMqttClient : MqttClient
{
    private readonly MqttClientOptions _mqttClientOptions;
    private readonly IMqttClient _mqttClient;
    private readonly Dictionary<string, List<Action<string>>> _handlers = new();

    public AvCodersMqttClient(string host, ushort port, string username, string password, string name):
        base(host, port, name)
    {
        _mqttClientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(Host, Port)
            .Build();
        _mqttClient = new MqttClientFactory().CreateMqttClient();
        _mqttClient.ConnectAsync(_mqttClientOptions, CancellationToken.None);
        _mqttClient.ConnectedAsync += RegisterDevicesToMqttServer;
        _mqttClient.DisconnectedAsync += HandleMqttDisconnection;
        _mqttClient.ApplicationMessageReceivedAsync += HandleMqttMessage;
    }
    private Task HandleMqttMessage(MqttApplicationMessageReceivedEventArgs arg)
    {
        string topic = arg.ApplicationMessage.Topic;
        var rawPayload = Encoding.UTF8.GetString(arg.ApplicationMessage.Payload);
        if(_handlers.TryGetValue(topic, out var handlers))
            foreach (var handler in handlers)
                handler(rawPayload);
        
        InvokeResponseHandlers($"{topic} - {rawPayload}");
        return Task.CompletedTask;
    }

    public override void SubscribeToTopic(string topic, Action<string> handler)
    {
        if (!_handlers.ContainsKey(topic))
        {
            _handlers.Add(topic, [handler]);
            _mqttClient.SubscribeAsync(topic, MqttQualityOfServiceLevel.AtLeastOnce);
        }
        _handlers[topic].Add(handler);
    }

    private async Task HandleMqttDisconnection(MqttClientDisconnectedEventArgs arg)
    {
        while (!_mqttClient.IsConnected)
        {
            ConnectionState = ConnectionState.Disconnected;
            Task.Delay(TimeSpan.FromSeconds(3)).Wait();
            ConnectionState = ConnectionState.Connecting;
            Log.Debug("Reconnecting to MQTT server");
            await _mqttClient.ConnectAsync(_mqttClientOptions, CancellationToken.None);
        }
    }

    private Task RegisterDevicesToMqttServer(MqttClientConnectedEventArgs arg)
    {
        ConnectionState = ConnectionState.Connected;
        _handlers.Keys.ToList().ForEach(topic => _mqttClient.SubscribeAsync(topic, MqttQualityOfServiceLevel.AtLeastOnce));
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
                Log.Verbose($"MqttClientNotConnectedException: {e}", EventLevel.Error);
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