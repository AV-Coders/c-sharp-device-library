using System.Net.Sockets;
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

public class TybaTurn2
{
    private readonly Uri _baseUri;
    private readonly Dictionary<string, string> _headers;
    private readonly ThreadWorker _streamWorker;
    protected CommunicationState CommunicationState = CommunicationState.Unknown;
    public LogHandler? LogHandlers;
    public CommunicationStateHandler? CommunicationStateHandlers;
    private string _currentEvent = String.Empty;
    public IntChangeHandler? LightSceneChangeHandlers;
    public IntChangeHandler? LightLevelChangeHandlers;
    public IntChangeHandler? ShadeChangeHandlers;
    public IntChangeHandler? FanSpeedChangeHandlers;
    public TemperatureChangeHandler? TemperatureChangeHandlers;
    private bool _streamConnected = false;
    private readonly Guid _thisInstanceGuid = Guid.NewGuid();
    private readonly Dictionary<string, IntChangeHandler?> _intChangeHandlerMap = new Dictionary<string, IntChangeHandler?>();


    public TybaTurn2(string ipAddress)
    {
        _baseUri = new Uri($"http://{ipAddress}:55555/api/v1.0/", UriKind.Absolute);
        _headers = new Dictionary<string, string>();
        _headers.Add("Sender-Id", _thisInstanceGuid.ToString());
        _headers.Add("Content-Type", "application/json");

        _streamWorker = new ThreadWorker(ConnectToTyba, TimeSpan.FromSeconds(30));
        _streamWorker.Restart();

    }

    private void ConnectToTyba()
    {
        if (!_streamConnected)
            CreateStream();
    }

    private async void CreateStream()
    {
        Log("Creating stream");
        _streamConnected = true;
        try
        {
            using (HttpClient httpClient = new HttpClient())
            {
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
        }
        catch (SocketException e)
        {
            LogHandlers?.Invoke(e.Message, EventLevel.Error);
            LogHandlers?.Invoke(e.StackTrace, EventLevel.Error);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }

        _streamConnected = false;
        Log("Stream Closed");
    }

    private void ProcessLine(string line)
    {
        Log(line, EventLevel.Verbose);
        if (line.Contains(": heartbeat"))
        {
            UpdateCommunicationState(CommunicationState.Okay);
            Log($"Heartbeat received");
            // TODO: Heartbeat logic goes here
            return;
        }
        
        if (line.Contains("event: "))
        {
            _currentEvent = line.Remove(0, 7);
            // Log($"Event received: {_currentEvent}");
        }
        else if (line.Contains("data: "))
        {
            if (_currentEvent.StartsWith("media")
                || _currentEvent.StartsWith("source")
                || line.Contains(_thisInstanceGuid.ToString())
                )
                return;
            ProcessEvent(line.Remove(0, 6));
        }
    }

    private void ProcessEvent(string line)
    {
        var eventData = _currentEvent.Split('/');
        
        StreamData? streamData = JsonSerializer.Deserialize<StreamData>(line);
        if(streamData == null)
            throw new InvalidDataException("Data is invalid");
        ValueAndOnChangeData? valueAndOnChangeData = null;
        TemperatureChangeData? temperatureChangeData = null;
        try
        {
            valueAndOnChangeData = streamData.State.Deserialize<ValueAndOnChangeData>();
        }
        catch (Exception)
        {
            // ignored
        }
        try
        {
            temperatureChangeData = streamData.State.Deserialize<TemperatureChangeData>();
        }
        catch (Exception)
        {
            // ignored
        }

        switch (eventData[0])
        {
            case "light_scenes":
                if(valueAndOnChangeData == null)
                    throw new InvalidDataException("Data is invalid");
                LightSceneChangeHandlers?.Invoke(int.Parse(eventData[1]), valueAndOnChangeData.Value);
                break;
            case "light":
                if(valueAndOnChangeData == null)
                    throw new InvalidDataException("Data is invalid");
                LightLevelChangeHandlers?.Invoke(int.Parse(eventData[1]), valueAndOnChangeData.Value);
                break;
            case "shade":
                if(valueAndOnChangeData == null)
                    throw new InvalidDataException("Data is invalid");
                ShadeChangeHandlers?.Invoke(int.Parse(eventData[1]), valueAndOnChangeData.Value);
                break;
            case "temperature":
                if(temperatureChangeData == null)
                    throw new InvalidDataException("Data is invalid");
                TemperatureChangeHandlers?.Invoke(int.Parse(eventData[1]), temperatureChangeData.Value);
                break;
            case "fan":
                if(valueAndOnChangeData == null)
                    throw new InvalidDataException("Data is invalid");
                FanSpeedChangeHandlers?.Invoke(int.Parse(eventData[1]), valueAndOnChangeData.Value);
                break;
        }
        Log(line);
    }

    public void SetShadeLevel(int shade, int level) => SetLevel("shade", shade, level);

    public void SetLightLevel(int light, int level) => SetLevel("light", light, level);

    public void SetLightScene(int scene) => SetLevel("light_scenes", 1, scene);

    private async void SetLevel(string type, int index, int value)
    {
        if (value > 100)
            return;
        Uri channelUri = new Uri(_baseUri, $"control/channels/{type}/{index}/1/state");
        string payload = $"{{\"value\": {value}}}";
        
        LogHandlers?.Invoke($"Sending payload {payload} to URI {channelUri.AbsoluteUri}");
        using (HttpClient httpClient = new HttpClient())
        {
            foreach (var (key, v) in _headers)
            {
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, v);
            }

            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            httpClient.DefaultRequestHeaders.ConnectionClose = false; // Keep the connection alive
            

            var response = await httpClient.PutAsync(channelUri, new StringContent(payload, Encoding.Default, "application/json"));
            LogHandlers?.Invoke($"Response {response.StatusCode}: {response.ReasonPhrase}");
        }
    }

    private void UpdateCommunicationState(CommunicationState state)
    {
        CommunicationState = state;
        CommunicationStateHandlers?.Invoke(state);
    }

    private void Log(string message, EventLevel level = EventLevel.Informational)
    {
        LogHandlers?.Invoke(message, level);
    }
}