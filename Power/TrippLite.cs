using System.Text.Json;
using AVCoders.Core;

namespace AVCoders.Power;

public class TrippLiteOutlet : Outlet
{
    private readonly string _id;
    private readonly int _loadNumber;
    private readonly TrippLitePdu _pdu;

    public TrippLiteOutlet(string name, string id, int loadNumber, TrippLitePdu pdu) : base(name)
    {
        _id = id;
        _loadNumber = loadNumber;
        _pdu = pdu;
    }

    public override void PowerOn()
    {
        throw new NotImplementedException();
    }

    public override void PowerOff()
    {
        throw new NotImplementedException();
    }

    public override void Reboot()
    {
        throw new NotImplementedException();
    }
}

public class TrippLitePdu: Pdu
{
    public const string DefaultUser = "localadmin";
    public const string DefaultPassword = "localadmin";
    public const ushort DefaultPort = 443;
    private readonly RestComms _restClient;
    private readonly string _username;
    private readonly string _password;
    private const string ContentType = "application/vnd.api+json";
    public LogHandler? LogHandlers;
    public CommunicationStateHandler? CommunicationStateHandlers;
    private CommunicationState _communicationState = CommunicationState.Unknown;
    private Action? _pendingAction;

    public TrippLitePdu(string name, RestComms restClient, string username, string password) : base(name)
    {
        _username = username;
        _password = password;
        _restClient = restClient;
        _restClient.AddDefaultHeader("By", "AV Coders");
        _restClient.AddDefaultHeader("Accept-Version", "1.0.0");
        _restClient.HttpResponseHandlers += HandleResponse;
        
        GetAuthToken();
        _pendingAction = GetLoadNames;
    }

    private void HandleResponse(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            CommunicationState = CommunicationState.Okay;
            ProcessResponseString(response);
            Log($"Response success!\r\nResponse data: {response.Content}");
            return;
        }

        CommunicationState = CommunicationState.Error;
        Log($"Response status code ({response.StatusCode.ToString()}) is not success, getting a new token\r\n Response data: {response.Content.ReadAsStringAsync()}");
        GetAuthToken();
        
    }

    private async Task ProcessResponseString(HttpResponseMessage response)
    {
        string responseBody = await response.Content.ReadAsStringAsync();
        JsonElement rootElement = JsonDocument.Parse(responseBody).RootElement;

        if (rootElement.TryGetProperty("access_token", out var accessToken))
        {
            _restClient.AddDefaultHeader("Authorization", "Bearer " + accessToken.GetString());
            Log("Access token retrieved");
            _pendingAction?.Invoke();
            return;
        }
        
        if (rootElement.TryGetProperty("data", out JsonElement dataElement) &&
            dataElement.TryGetProperty("attributes", out JsonElement attributesElement) &&
            attributesElement.TryGetProperty("load_action_supported", out var loadActionSupportedElement) &&
            loadActionSupportedElement.TryGetProperty("load_identity_per_device", out var loadEntities) &&
            loadEntities.TryGetProperty("loads", out var loads))
        {
            foreach (JsonElement load in loads.EnumerateArray())
            {
                if (load.TryGetProperty("name", out JsonElement nameElement) &&
                    load.TryGetProperty("id", out JsonElement idElement) &&
                    load.TryGetProperty("load_number", out JsonElement loadNumber))
                {
                    Outlets.Add(new TrippLiteOutlet(nameElement.GetString()!, idElement.GetString()!, loadNumber.GetInt32(), this));
                }
            }
        }
    }

    private void GetAuthToken()
    {
        Log("Getting auth token...");
        var payload = $"\\{{\"username\":\"{_username}\",\"password\":\"{_password}\",\"grant_type\": \"password\" \\}}";
        _restClient.Post(new Uri("/api/oauth/token", UriKind.Relative), payload, ContentType);
    }
    

    private void GetLoadNames()
    {
        Log("Getting load names");
        _restClient.Get(new Uri("/api/actions/supported"));
        if(_pendingAction == GetLoadNames)
            _pendingAction = null;
    }

    public override void PowerOn()
    {
        throw new NotImplementedException();
    }

    public override void PowerOff()
    {
        throw new NotImplementedException();
    }

    protected override Task Poll(CancellationToken token)
    {
        // _pendingAction = () => Poll(CancellationToken.None);
        return Task.CompletedTask;
    }
}