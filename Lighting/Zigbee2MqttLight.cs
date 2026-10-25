using System.Text.Json;
using AVCoders.Core;

namespace AVCoders.Lighting;

public class Zigbee2MqttLight : Light
{
    private readonly string _topic;
    private readonly MqttClient _client;
    private const string TopicPrefix = "zigbee2mqtt";
    private const string BrightnessProperty = "brightness";
    private const double MinimumBrightness = 0;
    private const double MaximumBrightness = 254;
    private const double PercentScale = 100;

    public Zigbee2MqttLight(string name, string topic, MqttClient client) : base(name)
    {
        _topic = topic;
        _client = client;

        _client.SubscribeToTopic($"{TopicPrefix}/{topic}", HandleValueChange);
    }

    private void HandleValueChange(string valueChange)
    {
        try
        {
            using var document = JsonDocument.Parse(valueChange);
            var data = document.RootElement;
            CommunicationState = CommunicationState.Okay;
            TryToGetBrightness(data);
            TryToGetPowerState(data);
        }
        catch (JsonException e)
        {
            LogException(e, $"Unable to parse {valueChange}");
        }
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
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(BrightnessProperty, out JsonElement brightness))
            return;
        if (brightness.ValueKind != JsonValueKind.Number || !brightness.TryGetDouble(out double value) || !double.IsInteger(value))
        {
            LogException(new FormatException($"Unable to read the brightness from {data.GetRawText()}"));
            return;
        }
        Brightness = ScaleBrightnessToPercentage(value);
    }

    public override List<LightPreset> Presets() => [];

    protected override void DoRecallPreset(int preset)
    {
        _client.Send($"{TopicPrefix}/{_topic}/set", $"{{\"scene_recall\": \"{preset}\"}}");
    }

    protected override void DoPowerOn() => _client.Send($"{TopicPrefix}/{_topic}/set", "{\"state\": \"ON\"}");

    protected override void DoPowerOff() => _client.Send($"{TopicPrefix}/{_topic}/set", "{\"state\": \"OFF\"}");

    protected override void DoSetLevel(int level)
    {
        byte scaled = (byte)Math.Clamp(Math.Round(level * MaximumBrightness / PercentScale), MinimumBrightness, MaximumBrightness);
        _client.Send($"{TopicPrefix}/{_topic}/set", $"{{\"state\": \"ON\", \"brightness\": {scaled}}}");
    }

    private uint ScaleBrightnessToPercentage(double value)
    {
        if (value is < MinimumBrightness or > MaximumBrightness)
            LogException(new ArgumentOutOfRangeException(nameof(value), value, $"Value must be between {MinimumBrightness} and {MaximumBrightness}."));

        return (uint)Math.Round(Math.Clamp(value, MinimumBrightness, MaximumBrightness) * PercentScale / MaximumBrightness);
    }
}