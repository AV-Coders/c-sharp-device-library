using System.Text.Json;
using AVCoders.Core;

namespace AVCoders.Camera;

public record OneBeyondLayout(string Id, string Name);
public record OneBeyondScenario(int Id, string Name);

public delegate void LayoutsChangedHandler(List<OneBeyondLayout> layouts);
public delegate void ScenariosChangedHandler(List<OneBeyondScenario> layouts);

public class AutomateVX : DeviceBase
{
    /// <summary>
    /// Legacy protocol (http) used for VX1 systems.
    /// </summary>
    public const string OldProtocol = "http";

    /// <summary>
    /// Default protocol (https) used for VX2 Systems.
    /// </summary>
    public const string DefaultProtocol = "https";
    /// <summary>
    /// Legacy port (3579) used for VX1 systems.
    /// </summary>
    public const ushort OldPort = 3579;

    /// <summary>
    /// Default port (4443) used for VX2 Systems.
    /// </summary>
    public const ushort DefaultPort = 4443;

    /// <summary>
    /// Password used for VX1 systems.  VX2 forces you to set a password at init
    /// </summary>
    public const string OldPassword = "1Beyond";
    
    private readonly RestComms _client;
    private string _token = string.Empty;
    private int _activeLayout = -1;
    private int _activeScenario = -1;
    private readonly Uri _tokenUri = new("/get-token", UriKind.Relative);
    private readonly Uri _startAutoSwitchUri = new("/api/StartAutoSwitch", UriKind.Relative);
    private readonly Uri _stopAutoSwitchUri = new("/api/StopAutoSwitch", UriKind.Relative);
    private readonly Uri _getLayoutsUri = new("/api/GetLayouts", UriKind.Relative);
    private readonly Uri _changeLayoutUri = new("/api/ChangeLayout", UriKind.Relative);
    private readonly Uri _getScenariosUri = new("/api/GetScenarios", UriKind.Relative);
    private readonly Uri _goToScenarioUri = new("/api/GoToScenario", UriKind.Relative);
    private readonly string _encodedUserAndPassword;
    private readonly List<OneBeyondLayout> _layouts = [];
    private readonly List<OneBeyondScenario> _scenarios = [];
    private readonly object _actionsLock = new();
    private readonly Dictionary<string, Action> _lastActions = new();
    private readonly List<Action> _actionsAwaitingToken = [];

    public LayoutsChangedHandler? LayoutsChangedHandlers;
    public ScenariosChangedHandler? ScenariosChangedHandlers;
    public IntHandler? ActiveScenarioChangedHandlers;
    public IntHandler? ActiveLayoutChangedHandlers;
    
    public List<OneBeyondScenario> Scenarios => _scenarios;
    public List<OneBeyondLayout> Layouts => _layouts;

    public int ActiveLayout
    {
        get => _activeLayout;
        protected set
        {
            if (_activeLayout == value)
                return;
            _activeLayout = value;
            ActiveLayoutChangedHandlers?.Invoke(_activeLayout);
        }
    }

    public int ActiveScenario
    {
        get => _activeScenario;
        protected set
        {
            if (_activeScenario == value)
                return;
            _activeScenario = value;
            ActiveScenarioChangedHandlers?.Invoke(_activeScenario);
        }
    }

    public AutomateVX(RestComms client, string username = "admin", string password = "1beyond") :
        base("1Beyond", client)
    {
        _client = client;

        _encodedUserAndPassword = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{username}:{password}"));

        _client.HttpResponseHandlers += async void (response) =>
        {
            try
            {
                await Handle1BeyondResponse(response);
            }
            catch (Exception e)
            {
                LogException(e);
            }
        };
        _actionsAwaitingToken.Add(GetEverything);
        GetToken();
    }

    private async Task Handle1BeyondResponse(HttpResponseMessage response)
    {
        using (PushProperties("Handle1BeyondResponse"))
        {
            if (!response.IsSuccessStatusCode)
            {
                CommunicationState = CommunicationState.Error;
                LogError("1Beyond gave an error status code {code} - {description}\n\n {body}",
                    response.StatusCode, response.StatusCode.ToString(), response.Content.ReadAsStringAsync().Result);
                return;
            }

            var path = response.RequestMessage?.RequestUri?.AbsolutePath;
            var responseContent = await response.Content.ReadAsStringAsync();
            try
            {
                using var document = JsonDocument.Parse(responseContent);
                var root = document.RootElement;

                switch (root.TryGetProperty("status", out JsonElement statusElement))
                {
                    case false:
                        LogError("1Beyond did not return a status");
                        return;
                    case true:
                        switch (statusElement.GetString())
                        {
                            case "OK":
                                CommunicationState = CommunicationState.Okay;
                                break;
                            case "Error":
                                Handle1BeyondError(root, path);
                                return;
                            default:
                                LogError("1Beyond returned an unknown status");
                                return;
                        }
                        break;
                }

                if (root.TryGetProperty("token", out JsonElement tokenElement))
                {
                    LogVerbose("1Beyond token received");
                    _token = tokenElement.GetString() ?? string.Empty;
                    _client.AddDefaultHeader("Authorization", _token);
                    SendActionsAwaitingToken();
                }
                else if (root.TryGetProperty("layouts", out JsonElement layoutsElement))
                {
                    _layouts.Clear();
                    foreach (var layout in layoutsElement.EnumerateArray())
                    {
                        var id = layout.GetProperty("id").GetString();
                        var name = layout.GetProperty("name").GetString();
                        if (id != null && name != null)
                        {
                            _layouts.Add(new OneBeyondLayout(id, name));
                        }
                    }
                    LayoutsChangedHandlers?.Invoke(_layouts);
                    LogVerbose("Found {layoutCount} layouts", _layouts.Count);
                }
                else if (root.TryGetProperty("scenarios", out JsonElement scenariosElement))
                {
                    _scenarios.Clear();
                    foreach (var scenario in scenariosElement.EnumerateArray())
                    {
                        var id = scenario.GetProperty("id").GetInt32();
                        var name = scenario.GetProperty("name").GetString();
                        if (name != null)
                        {
                            _scenarios.Add(new OneBeyondScenario(id, name));
                        }
                    }
                    ScenariosChangedHandlers?.Invoke(_scenarios);
                    LogVerbose("Found {scenarioCount} scenarios", _scenarios.Count);
                }
                else if (root.TryGetProperty("message", out JsonElement messageElement))
                {
                    var message = messageElement.GetString();
                    if (message?.StartsWith("Changed to Layout") == true)
                    {
                        var layoutId = message[^1] - 'A';  // Convert layout letter (A, B, C) to index (0, 1, 2)
                        ActiveLayout = layoutId;
                    }
                    else if (message?.StartsWith("Successfully called scenario") == true)
                    {
                        var scenarioNumberStr = message.Split(' ')[^1];
                        if (int.TryParse(scenarioNumberStr, out int scenarioNumber))
                        {
                            ActiveScenario = scenarioNumber - 1;  // Convert from 1-based to 0-based index
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                LogError(ex, "Failed to parse 1Beyond response: {content}", responseContent);
            }
        }
    }

    private void Handle1BeyondError(JsonElement root, string? path)
    {
        CommunicationState = CommunicationState.Error;
        if(root.TryGetProperty("status", out JsonElement statusElement))
        {
            LogError("1Beyond returned a status of {status}", statusElement.GetString());
            if (root.TryGetProperty("err", out JsonElement errElement))
            {
                LogError("1Beyond error: {oneBeyondErrorText}", errElement.GetString());
                if (errElement.GetString() == "Unauthorized")
                {
                    RetryAfterToken(path);
                    GetToken();
                }
            }
        }
    }

    private void RetryAfterToken(string? path)
    {
        lock (_actionsLock)
        {
            if (path != null && _lastActions.TryGetValue(path, out var action))
            {
                _actionsAwaitingToken.Add(action);
                return;
            }
        }
        LogWarning("The unauthorized reply to {path} matches no request, so nothing is retried", path);
    }

    private void SendActionsAwaitingToken()
    {
        Action[] actions;
        lock (_actionsLock)
        {
            actions = [.._actionsAwaitingToken];
            _actionsAwaitingToken.Clear();
        }
        foreach (var action in actions)
            action.Invoke();
    }

    private void Post(Uri uri, string payload, Action action)
    {
        lock (_actionsLock)
            _lastActions[uri.OriginalString] = action;
        _client.Post(uri, payload, "application/json");
    }

    private void GetEverything()
    {
        GetLayouts();
        GetScenarios();
    }

    private void GetToken()
    {
        using (PushProperties("GetToken"))
        {
            LogInformation("Getting Token from 1Beyond");
            _client.AddDefaultHeader("Authorization", _encodedUserAndPassword);
            _client.Post(_tokenUri, "", "application/json");
        }
    }

    public void StartAutoSwitch()
    {
        using (PushProperties("StartAutoSwitch"))
        {
            LogInformation("Starting auto switch");
            Post(_startAutoSwitchUri, "", StartAutoSwitch);
        }
    }

    public void StopAutoSwitch()
    {
        using (PushProperties("StopAutoSwitch"))
        {
            LogInformation("Stopping auto switch");
            Post(_stopAutoSwitchUri, "", StopAutoSwitch);
        }
    }

    private void GetLayouts()
    {
        using (PushProperties("GetLayouts"))
        {
            LogInformation("Getting Layouts");
            Post(_getLayoutsUri, "", GetLayouts);
        }
    }

    public void SetLayout(int layoutId)
    {
        using (PushProperties("SetLayout"))
        {
            if (layoutId < 0 || layoutId >= _layouts.Count)
            {
                LogWarning("Layout {layoutId} is out of range, {layoutCount} layouts are loaded", layoutId, _layouts.Count);
                AddEvent(EventType.Error, $"Layout {layoutId} is out of range, {_layouts.Count} layouts are loaded");
                return;
            }
            LogInformation("Setting Layout to {layoutId}", _layouts[layoutId].Name);
            Post(_changeLayoutUri, $"{{\"id\": \"{_layouts[layoutId].Id}\"}}", () => SetLayout(layoutId));
        }
    }

    private void GetScenarios()
    {
        using (PushProperties("GetScenarios"))
        {
            LogInformation("Getting Scenarios");
            Post(_getScenariosUri, "", GetScenarios);
        }
    }

    public void SetScenario(int scenarioId)
    {
        using (PushProperties("SetScenario"))
        {
            if (scenarioId < 0 || scenarioId >= _scenarios.Count)
            {
                LogWarning("Scenario {scenarioId} is out of range, {scenarioCount} scenarios are loaded", scenarioId, _scenarios.Count);
                AddEvent(EventType.Error, $"Scenario {scenarioId} is out of range, {_scenarios.Count} scenarios are loaded");
                return;
            }
            LogInformation("Setting Scenario to {scenarioName}", _scenarios[scenarioId].Name);
            Post(_goToScenarioUri, $"{{\"id\": \"{_scenarios[scenarioId].Id}\"}}", () => SetScenario(scenarioId));
        }
    }

    public void SetScenarioById(int id)
    {
        using (PushProperties("SetScenarioById"))
        {
            LogInformation("Setting Scenario to id {scenarioId}", id);
            Post(_goToScenarioUri, $"{{\"id\": \"{id}\"}}", () => SetScenarioById(id));
        }
    }

    public override void PowerOn()
    {
    }

    public override void PowerOff()
    {
    }
}