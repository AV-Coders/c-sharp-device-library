using System.Text.Json;
using AVCoders.Core;

namespace AVCoders.Lighting;

public class Zigbee2MqttLight : Light
{
    private readonly string _topic;
    private readonly MqttClient _client;
    private const string TopicPrefix = "zigbee2mqtt";
    private const string BrightnessProperty = "brightness";
    private const string LinkQualityProperty = "linkquality";
    private const string UpdateProperty = "update";
    private const string UpdateStateProperty = "state";
    private const string LinkQualityLabel = "Link Quality";
    private const string UpdateLabel = "Firmware Update";
    private const string LinkQualityIssueKey = "linkquality-read";
    private const string UpdateIssueKey = "update-read";
    private const double MinimumBrightness = 0;
    private const double MaximumBrightness = 254;
    private const double PercentScale = 100;
    private const int MinimumLinkQuality = 0;
    private const int MaximumLinkQuality = 255;
    private const int MaxMissedReplies = 3;
    private const string StateRequest = "{\"state\": \"\", \"brightness\": \"\"}";
    private readonly Dictionary<string, int> _missedReplies = new();

    public Zigbee2MqttLight(string name, string topic, MqttClient client) : base(name)
    {
        _topic = topic;
        _client = client;

        _client.SubscribeToTopic($"{TopicPrefix}/{topic}", HandleValueChange);
        _client.ConnectionStateHandlers += HandleConnectionState;
        HandleConnectionState(_client.ConnectionState);
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState != ConnectionState.Connected)
            return;
        _client.Send($"{TopicPrefix}/{_topic}/get", StateRequest);
    }

    private void HandleValueChange(string valueChange)
    {
        using (PushProperties())
        {
            try
            {
                using var document = JsonDocument.Parse(valueChange);
                var data = document.RootElement;
                CommunicationState = CommunicationState.Okay;
                TryToGetBrightness(data);
                TryToGetPowerState(data);
                ShowLinkQuality(data);
                ShowUpdateState(data);
            }
            catch (JsonException e)
            {
                LogException(e, $"Unable to parse {valueChange}");
            }
        }
    }

    private void ShowLinkQuality(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(LinkQualityProperty, out JsonElement linkQuality))
            return;
        SetDetail(LinkQualityLabel, linkQuality.GetRawText());
        if (IsEmpty(linkQuality) || linkQuality.ValueKind == JsonValueKind.Number && linkQuality.TryGetInt32(out int value)
            && value is >= MinimumLinkQuality and <= MaximumLinkQuality)
            ReadAnswered(LinkQualityIssueKey);
        else
            CountMissedReply(LinkQualityIssueKey,
                $"a {LinkQualityProperty} from {MinimumLinkQuality} to {MaximumLinkQuality}", linkQuality.GetRawText());
    }

    private void ShowUpdateState(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(UpdateProperty, out JsonElement update))
            return;
        if (update.ValueKind == JsonValueKind.Object && update.TryGetProperty(UpdateStateProperty, out JsonElement state)
            && state.ValueKind == JsonValueKind.String && state.GetString() is { Length: > 0 } text)
        {
            SetDetail(UpdateLabel, text);
            ReadAnswered(UpdateIssueKey);
            return;
        }
        SetDetail(UpdateLabel, update.GetRawText());
        if (IsEmpty(update) || update.ValueKind == JsonValueKind.Object
            && (!update.TryGetProperty(UpdateStateProperty, out JsonElement stateValue) || IsEmpty(stateValue)))
            ReadAnswered(UpdateIssueKey);
        else
            CountMissedReply(UpdateIssueKey, $"an {UpdateProperty} with a text {UpdateStateProperty}", update.GetRawText());
    }

    private static bool IsEmpty(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.String && value.GetString()!.Length == 0;

    private void CountMissedReply(string issueKey, string expected, string raw)
    {
        var missed = _missedReplies.GetValueOrDefault(issueKey) + 1;
        _missedReplies[issueKey] = missed;
        if (missed < MaxMissedReplies)
        {
            LogInformation("The light sent {Value} instead of {Expected} ({Missed} of {MaxMissedReplies})",
                raw, expected, missed, MaxMissedReplies);
            return;
        }
        RaiseOngoingIssue(issueKey,
            $"The light sent {MaxMissedReplies} messages in a row without {expected}, the last was {raw}", IssueSeverity.Minor);
    }

    private void ReadAnswered(string issueKey)
    {
        _missedReplies.Remove(issueKey);
        ResolveIssue(issueKey);
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