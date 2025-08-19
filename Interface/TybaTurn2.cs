using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AVCoders.Core;
using Serilog;

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

public class TybaTurn2 : LogBase
{
    private readonly Uri _baseUri;
    private readonly Dictionary<string, string> _headers;
    protected CommunicationState CommunicationState = CommunicationState.Unknown;
    public CommunicationStateHandler? CommunicationStateHandlers;
    private string _currentEvent = String.Empty;
    public IntChangeHandler? LightSceneChangeHandlers;
    public IntChangeHandler? LightLevelChangeHandlers;
    public IntChangeHandler? ShadeChangeHandlers;
    public IntChangeHandler? FanSpeedChangeHandlers;
    public IntChangeHandler? ClimateModeChangeHandlers;
    public TemperatureChangeHandler? TemperatureChangeHandlers;
    private bool _streamConnected = false;
    private readonly Guid _thisInstanceGuid = Guid.NewGuid();


    public TybaTurn2(string ipAddress, string name) : base(name)
    {
        _baseUri = new Uri($"http://{ipAddress}:55555/api/v1.0/", UriKind.Absolute);
        _headers = new Dictionary<string, string>();
        _headers.Add("Sender-Id", _thisInstanceGuid.ToString());
        _headers.Add("Content-Type", "application/json");

        var streamWorker = new ThreadWorker(ConnectToTyba, TimeSpan.FromSeconds(30));
        streamWorker.Restart();
    }

    private async Task ConnectToTyba(CancellationToken obj)
    {
        if (!_streamConnected)
            await CreateStream();
    }

    private async Task CreateStream()
    {
        _streamConnected = true;
        try
        {
            using HttpClient httpClient = new HttpClient();
            foreach (var (key, value) in _headers)
            {
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
            }

            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/event-stream");
            httpClient.DefaultRequestHeaders.ConnectionClose = false; // Keep the connection alive
            Uri requestUri = new Uri(_baseUri, "control/events/channels");

            var stream = await httpClient.GetStreamAsync(requestUri);
            var streamReader = new StreamReader(stream);

            while (!streamReader.EndOfStream)
            {
                var line = await streamReader.ReadLineAsync();
                if (!string.IsNullOrEmpty(line))
                {
                    ProcessLine(line);
                }
            }
        }
        catch (SocketException e)
        {
            LogException(e);
            UpdateCommunicationState(CommunicationState.Error);
        }
        catch (IOException e)
        {
            LogException(e);
            UpdateCommunicationState(CommunicationState.Error);
        }
        catch (HttpRequestException e)
        {
            LogException(e);
            UpdateCommunicationState(CommunicationState.Error);
        }
        catch (Exception e)
        {
            LogException(e);
            throw;
        }

        _streamConnected = false;
    }

    private void ProcessLine(string line)
    {
        if (line.Contains(": heartbeat"))
        {
            UpdateCommunicationState(CommunicationState.Okay);
            Log.Debug("Heartbeat received");
            // TODO: Heartbeat logic goes here
            return;
        }
        
        if (line.Contains("event: "))
        {
            _currentEvent = line.Remove(0, 7);
            Log.Debug("Event received: {CurrentEvent}", _currentEvent);
        }
        else if (line.Contains("data: "))
        {
            if (_currentEvent.StartsWith("media")
                || _currentEvent.StartsWith("source")
                || line.Contains(_thisInstanceGuid.ToString())
                || line.Contains("InternalTemperatureServiceImpl")
                )
                return;
            Log.Verbose(line);
            ProcessEvent(line.Remove(0, 6));
            _currentEvent = String.Empty;
        }
    }

    private void ProcessEvent(string line)
    {
        if (_currentEvent == String.Empty)
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
            if (valueAndOnChangeData == null && eventData[0] != "temperature")
            {
                Log.Debug("Data is invalid");
                return;
            }

            if (temperatureChangeData == null && eventData[0] == "temperature")
            {
                Log.Debug("Data is invalid");
                return;
            }

            handler(int.Parse(eventData[1]));
        }
        else
        {
            Log.Error("Unhandled event type: {EventType}", eventData[0]);
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

    public void SetShadeLevel(int shade, int level) => _ = SetLevel("shade", shade, level);

    public void SetLightLevel(int light, int level) => _ = SetLevel("light", light, level);

    public void SetLightScene(int scene) => _ = SetLevel("light_scenes", 1, scene);

    public void SetClimateMode(int level) => _ = SetLevel("modes", 1, level);

    public void SetClimateFanSpeed(int level) => _ = SetLevel("fan", 1, level);
    public void SetClimateTargetTemperature(double level) => _ = SetLevel("temperature", 1, level);
    public void SetClimateCurrentTemperature(double level) => _ = SetLevel("temperature", 2, level);

    private async Task SetLevel(string type, int index, int value)
    {
        if (value > 100)
            return;
        Uri channelUri = new Uri(_baseUri, $"control/channels/{type}/{index}/1/state");
        string payload = $"{{\"value\": {value}}}";
        await Put(payload, channelUri);
        
    }

    private async Task SetLevel(string type, int index, double value)
    {
        if (value > 100)
            return;
        Uri channelUri = new Uri(_baseUri, $"control/channels/{type}/1/{index}/state");
        string payload = $"{{\"value\": {value}}}";
        
        await Put(payload, channelUri);
    }

    private async Task Put(string payload, Uri channelUri)
    {
        
        try
        {
            Log.Verbose("Sending payload {Payload} to URI {ChannelUriAbsoluteUri}", payload, channelUri.AbsoluteUri);
            using HttpClient httpClient = new HttpClient();
            foreach (var (key, v) in _headers)
            {
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, v);
            }

            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");

            var response = await httpClient.PutAsync(channelUri, new StringContent(payload, Encoding.Default, "application/json"));
            Log.Verbose("Response {ResponseStatusCode}: {ResponseReasonPhrase}", response.StatusCode, response.ReasonPhrase);
        }
        catch (HttpRequestException e)
        {
            LogException(e);
            UpdateCommunicationState(CommunicationState.Error);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }

    private void UpdateCommunicationState(CommunicationState state)
    {
        CommunicationState = state;
        CommunicationStateHandlers?.Invoke(state);
    }
}