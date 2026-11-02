using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AVCoders.Core;

namespace AvCoders.Interface;

public delegate void IntChangeHandler(int index, int value);
public delegate void TemperatureChangeHandler(int shade, double temperature);

public record StreamData(
    [property: JsonPropertyName("modifiedBy")]
    string ModifiedBy,
    [property: JsonPropertyName("state")]
    JsonElement State
    );

public record ValueAndOnChangeData(
    [property: JsonPropertyName("value")]
    int Value,
    [property: JsonPropertyName("on")]
    string On
);

public record TemperatureChangeData(
    [property: JsonPropertyName("value")]
    double Value
);

public class TybaTurn2 : LogBase, IDisposable
{
    public const ushort DefaultPort = 55555;
    private const string Heartbeat = ": heartbeat";
    private const string EventField = "event:";
    private const string DataField = "data:";
    private const int MaxLevel = 100;
    private const string AddressIssueKey = "address";

    private readonly Uri? _baseUri;
    private readonly string _ipAddress;
    private readonly Dictionary<string, string> _headers;
    private readonly ThreadWorker _streamWorker;
    protected CommunicationState CommunicationState = CommunicationState.Unknown;
    public CommunicationStateHandler? CommunicationStateHandlers;
    private string _currentEvent = string.Empty;
    public IntChangeHandler? LightSceneChangeHandlers;
    public IntChangeHandler? LightLevelChangeHandlers;
    public IntChangeHandler? ShadeChangeHandlers;
    public IntChangeHandler? FanSpeedChangeHandlers;
    public IntChangeHandler? ClimateModeChangeHandlers;
    public TemperatureChangeHandler? TemperatureChangeHandlers;
    private bool _streamConnected = false;
    private readonly Guid _thisInstanceGuid = Guid.NewGuid();


    public TybaTurn2(string ipAddress, string name) : this(ipAddress, name, DefaultPort)
    {
    }

    public TybaTurn2(string ipAddress, string name, ushort port) : base(name)
    {
        _ipAddress = ipAddress;
        _headers = new Dictionary<string, string>();
        _headers.Add("Sender-Id", _thisInstanceGuid.ToString());
        _headers.Add("Content-Type", "application/json");
        _streamWorker = new ThreadWorker(ConnectToTyba, TimeSpan.FromSeconds(30));

        if (!Uri.TryCreate($"http://{ipAddress}:{port}/api/v1.0/", UriKind.Absolute, out var baseUri))
        {
            RaiseOngoingIssue(AddressIssueKey,
                $"The address {ipAddress} does not make a valid URI, so the Tyba is not contacted",
                IssueSeverity.Critical);
            return;
        }
        _baseUri = baseUri;
        _streamWorker.Restart();
    }

    public void Dispose()
    {
        Task.Run(_streamWorker.Stop).GetAwaiter().GetResult();
        LogBaseRegistry.Deregister(_streamWorker);
        LogBaseRegistry.Deregister(this);
        GC.SuppressFinalize(this);
    }

    private async Task ConnectToTyba(CancellationToken token)
    {
        if (!_streamConnected)
            await CreateStream(token);
    }

    private async Task CreateStream(CancellationToken token)
    {
        _streamConnected = true;
        try
        {
            using HttpClient httpClient = new HttpClient(new SocketsHttpHandler { ActivityHeadersPropagator = null });
            foreach (var (key, value) in _headers)
            {
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
            }

            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/event-stream");
            httpClient.DefaultRequestHeaders.ConnectionClose = false; // Keep the connection alive
            Uri requestUri = new Uri(_baseUri!, "control/events/channels");

            using var stream = await httpClient.GetStreamAsync(requestUri, token);
            using var streamReader = new StreamReader(stream);

            while (await streamReader.ReadLineAsync(token) is { } line)
            {
                if (!string.IsNullOrEmpty(line))
                {
                    using (Trigger.Begin())
                        ProcessLine(line);
                }
            }
        }
        catch (Exception e) when (!token.IsCancellationRequested)
        {
            LogException(e);
            UpdateCommunicationState(CommunicationState.Error);
        }

        _streamConnected = false;
    }

    private void ProcessLine(string line)
    {
        using (PushProperties("ProcessLine"))
        {
            if (line.StartsWith(Heartbeat, StringComparison.Ordinal))
            {
                UpdateCommunicationState(CommunicationState.Okay);
                LogDebug("Heartbeat received");
                // TODO: Heartbeat logic goes here
                return;
            }

            if (line.StartsWith(EventField, StringComparison.Ordinal))
            {
                _currentEvent = FieldValue(line, EventField);
                LogDebug("Event received: {CurrentEvent}", _currentEvent);
            }
            else if (line.StartsWith(DataField, StringComparison.Ordinal))
            {
                if (_currentEvent.StartsWith("media")
                    || _currentEvent.StartsWith("source")
                    || line.Contains(_thisInstanceGuid.ToString())
                    || line.Contains("InternalTemperatureServiceImpl")
                   )
                    return;
                LogVerbose("{Line}", line);
                try
                {
                    ProcessEvent(FieldValue(line, DataField));
                }
                catch (Exception e)
                {
                    LogException(e, $"Unable to process {line}");
                    UpdateCommunicationState(CommunicationState.Error);
                    return;
                }
                _currentEvent = string.Empty;
            }
        }
    }

    private static string FieldValue(string line, string field)
    {
        var value = line[field.Length..];
        return value.StartsWith(' ') ? value[1..] : value;
    }

    private void ProcessEvent(string line)
    {
        if (_currentEvent == string.Empty)
            return;

        var eventData = _currentEvent.Split('/');

        StreamData? streamData = JsonSerializer.Deserialize<StreamData>(line);
        if(streamData == null)
            throw new InvalidDataException("Data is invalid");
        ValueAndOnChangeData? valueAndOnChangeData = Deserialise<ValueAndOnChangeData>(streamData.State);
        TemperatureChangeData? temperatureChangeData = Deserialise<TemperatureChangeData>(streamData.State);

        var handlers = new Dictionary<string, Action<int>>
        {
            { "light_scenes", index => LightSceneChangeHandlers?.Invoke(index, valueAndOnChangeData!.Value) },
            { "light", index => LightLevelChangeHandlers?.Invoke(index, valueAndOnChangeData!.Value) },
            { "shade", index => ShadeChangeHandlers?.Invoke(index, valueAndOnChangeData!.Value) },
            { "fan", index => FanSpeedChangeHandlers?.Invoke(index, valueAndOnChangeData!.Value) },
            { "modes", index => ClimateModeChangeHandlers?.Invoke(index, valueAndOnChangeData!.Value) },
            { "temperature", index => TemperatureChangeHandlers?.Invoke(index, temperatureChangeData!.Value) }
        };

        if (handlers.TryGetValue(eventData[0], out var handler))
        {
            if (valueAndOnChangeData == null && eventData[0] != "temperature" || temperatureChangeData == null && eventData[0] == "temperature")
            {
                LogDebug("Data is invalid");
                return;
            }

            if (eventData.Length < 2 ||
                !int.TryParse(eventData[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                LogWarning("Event {Event} has no channel index", _currentEvent);
                return;
            }

            handler(index);
        }
        else
        {
            LogError("Unhandled event type: {EventType}", eventData[0]);
        }
    }

    private static T? Deserialise<T>(JsonElement element)
    {
        try
        {
            return element.Deserialize<T>();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public void ShadeFeedback(int shade, int level) => _ = SetLevel("shade", shade, level);

    public void LightLevelFeedback(int light, int level) => _ = SetLevel("light", light, level);

    public void LightSceneFeedback(int scene) => _ = SetLevel("light_scenes", 1, scene);

    public void ClimateModeFeedback(int level) => _ = SetLevel("modes", 1, level);

    public void ClimateFanFeedback(int level) => _ = SetLevel("fan", 1, level);
    public void ClimateTargetFeedback(double level) => _ = SetLevel("temperature", 1, level);
    public void ClimateAmbientFeedback(double level) => _ = SetLevel("temperature", 2, level);

    private async Task SetLevel(string type, int index, int value)
    {
        if (value > MaxLevel)
        {
            LogWarning("{Type} {Index} value {Value} is above {MaxLevel}, not sent", type, index, value, MaxLevel);
            return;
        }
        string payload = $"{{\"value\": {value}}}";
        await Put(payload, $"control/channels/{type}/{index}/1/state");

    }

    private async Task SetLevel(string type, int index, double value)
    {
        if (value > MaxLevel)
        {
            LogWarning("{Type} {Index} value {Value} is above {MaxLevel}, not sent", type, index, value, MaxLevel);
            return;
        }
        string payload = $"{{\"value\": {value.ToString(CultureInfo.InvariantCulture)}}}";

        await Put(payload, $"control/channels/{type}/1/{index}/state");
    }

    private async Task Put(string payload, string channelPath)
    {
        if (_baseUri == null)
        {
            LogError("Payload {Payload} for {ChannelPath} not sent, {IpAddress} is not a valid address", payload,
                channelPath, _ipAddress);
            return;
        }
        Uri channelUri = new Uri(_baseUri, channelPath);
        try
        {
            LogVerbose("Sending payload {Payload} to URI {ChannelUriAbsoluteUri}", payload, channelUri.AbsoluteUri);
            using HttpClient httpClient = new HttpClient(new SocketsHttpHandler { ActivityHeadersPropagator = null });
            foreach (var (key, v) in _headers)
            {
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, v);
            }

            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");

            using var response = await httpClient.PutAsync(channelUri, new StringContent(payload, Encoding.Default, "application/json"));
            if (!response.IsSuccessStatusCode)
            {
                LogError("Response {ResponseStatusCode}: {ResponseReasonPhrase} from {ChannelUriAbsoluteUri}",
                    (int)response.StatusCode, response.ReasonPhrase, channelUri.AbsoluteUri);
                UpdateCommunicationState(CommunicationState.Error);
                return;
            }
            LogVerbose("Response {ResponseStatusCode}: {ResponseReasonPhrase}", response.StatusCode, response.ReasonPhrase);
        }
        catch (Exception e)
        {
            LogException(e);
            UpdateCommunicationState(CommunicationState.Error);
        }
    }

    private void UpdateCommunicationState(CommunicationState state)
    {
        CommunicationState = state;
        CommunicationStateHandlers?.Invoke(state);
    }
}
