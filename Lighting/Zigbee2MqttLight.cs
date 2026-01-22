using System.Text.Json;
using AVCoders.Core;

namespace AVCoders.Lighting;

public class Zigbee2MqttLight : Light
{
    private readonly string _topic;
    private readonly MqttClient _client;
    private const string TopicPrefix = "zigbee2mqtt";
    
    public Zigbee2MqttLight(string name, string topic, MqttClient client) : base(name)
    {
        _topic = topic;
        _client = client;

        _client.SubscribeToTopic($"{TopicPrefix}/{topic}", HandleValueChange);
    }

    private void HandleValueChange(string valueChange)
    {
        var data = JsonSerializer.Deserialize<JsonElement>(valueChange);
        TryToGetBrightness(data);
        TryToGetPowerState(data);
    }

    private void TryToGetPowerState(JsonElement data)
    {
        try
        {
            PowerState = data.GetProperty("state").GetString()?.ToUpper() switch
            {
                "ON" => PowerState.On,
                "OFF" => PowerState.Off,
                _ => PowerState.Unknown
            };
        }
        catch (Exception e)
        {
            LogException(e);
        }
    }

    private void TryToGetBrightness(JsonElement data)
    {
        try
        {
            Brightness = ScaleByteToPercentage(data.GetProperty("brightness").GetInt32());
        }
        catch (Exception e)
        {
            LogException(e);
        }
    }

    protected override void DoRecallPreset(int preset)
    {
        _client.Send($"{TopicPrefix}/{_topic}/set", $"{{\"scene_recall\": \"{preset}\"}}");
    }

    protected override void DoPowerOn() => _client.Send($"{TopicPrefix}/{_topic}/set", "{\"state\": \"On\"}");

    protected override void DoPowerOff() => _client.Send($"{TopicPrefix}/{_topic}/set", "{\"state\": \"Off\"}");

    protected override void DoSetLevel(int level)
    {
        byte scaled = (byte)Math.Clamp((int)Math.Round(level * 255.0 / 100.0), 0, 255);
        _client.Send($"{TopicPrefix}/{_topic}/set", $"{{\"state\": \"On\", \"brightness\": {scaled}}}");
    }

    private uint ScaleByteToPercentage(int value)
    {
        switch (value)
        {
            case 0: return 0;
            case 255: return 100;
        }

        if (value is < 0 or > 255)
        {
            LogException(new ArgumentOutOfRangeException(nameof(value), "Value must be between 0 and 255."));
        }

        return (uint)(value * 100) / 255;
    }
}