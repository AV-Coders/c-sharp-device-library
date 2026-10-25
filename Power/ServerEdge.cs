using System.Text;
using System.Xml;
using AVCoders.Core;

namespace AVCoders.Power;

public class ServerEdgeOutlet : Outlet
{
    private readonly ServerEdgePdu _pdu;

    public ServerEdgeOutlet(string name, ServerEdgePdu pdu) : base(name)
    {
        _pdu = pdu;
    }
    
    public void SetName(string name) => Name = name;

    public override void PowerOn() => _pdu.TurnOnOutlet(this);

    public override void PowerOff() => _pdu.TurnOffOutlet(this);

    public override void Reboot() => _pdu.RebootOutlet(this);
}

public class ServerEdgePdu: Pdu, IDisposable
{
    public const string DefaultUser = "snmp";
    public const string DefaultPassword = "1234";
    private const string NoReply = "no reply";
    private const string AllOutlets = "all outlets";
    private readonly RestComms _restClient;
    private readonly Uri _getNamesUri;
    private readonly Uri _statusUri;
    private readonly string _onUri = "/ons.cgi?led=";
    private readonly string _offUri = "/offs.cgi?led=";
    private readonly string _rebootUri = "/offon.cgi?led=";
    private readonly ThreadWorker _pollWorker;
    private readonly object _requestsLock = new();
    private Task _requests = Task.CompletedTask;
    private volatile bool _disposed;
    private volatile string _lastReply = NoReply;

    public ServerEdgePdu(RestComms restClient, string name, string username, string password, int numberOfOutlets) 
        : base(name, restClient)
    {
        _restClient = restClient;
        string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
        _restClient.AddDefaultHeader("Authorization", $"Basic {credentials}");
        _restClient.HttpResponseHandlers += HandleResponse;
        _restClient.ConnectionStateHandlers += HandleConnectionState;
        
        _getNamesUri = new Uri("/Getname.xml", UriKind.Relative);
        _statusUri = new Uri("/status.xml", UriKind.Relative);

        for (int i = 0; i < numberOfOutlets; i++)
        {
            AddOutlet(new ServerEdgeOutlet("Unknown", this));
        }

        _ = Enqueue(() => _restClient.Get(_getNamesUri));

        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(6));
        _pollWorker.Restart();

    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState != ConnectionState.Error)
            return;
        CommunicationState = CommunicationState.Error;
    }

    private void HandleResponse(HttpResponseMessage response)
    {
        string responseString = response.Content.ReadAsStringAsync().Result;
        _lastReply = $"HTTP {(int)response.StatusCode}: {responseString}";
        if (!response.IsSuccessStatusCode)
        {
            ReportUnusableResponse($"HTTP {(int)response.StatusCode}", responseString);
            return;
        }
        if (responseString.Contains("Success!"))
        {
            CommunicationState = CommunicationState.Okay;
            return;
        }
        try
        {
            XmlDocument xmlDoc = new XmlDocument();
            xmlDoc.LoadXml(responseString);
            string namePayload = string.Empty;

            foreach (XmlNode childNode in xmlDoc.DocumentElement!.ChildNodes)
            {
                if (childNode.Name.StartsWith("pot"))
                {
                    // <pot0>,,0.3,0.0,0.0,0.0,0.0,0.0,0.0,0.0,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,0,5,5,5,5,5,5,5,0.3,0,</pot0>
                    var rawFlags = childNode.InnerText.Split(',');
                    for (int i = 0; i < Outlets.Count; i++)
                    {
                        Outlets[i].OverridePowerState(rawFlags[i+10] == "1" ? PowerState.On : PowerState.Off);
                    }
                }
                else if (childNode.Name.StartsWith("na"))
                {
                    namePayload += childNode.InnerText;
                }
            }
        
            if(namePayload != string.Empty)
                ProcessOutletNames(namePayload);
        }
        catch (Exception e) when (e is XmlException or IndexOutOfRangeException)
        {
            LogException(e);
            ReportUnusableResponse(e.Message, responseString);
            return;
        }
        catch (Exception e)
        {
            LogException(e, "An outlet handler threw");
            AddEvent(EventType.Error, $"An outlet handler threw: {e.Message}");
        }
        CommunicationState = CommunicationState.Okay;
    }

    private void ReportUnusableResponse(string reason, string body)
    {
        AddEvent(EventType.Error, $"The response could not be used, {reason}: {body}");
        CommunicationState = CommunicationState.Error;
    }

    private void ProcessOutletNames(string namePayload)
    {
        string[] names = namePayload.Split(',');
        for (int i = 0; i < Outlets.Count; i++)
        {
            var outlet1 = (ServerEdgeOutlet)Outlets[i];
            outlet1.SetName(names[GetNameIndex(i)]);
        }
        
        OutletDefinitionHandlers?.Invoke(Outlets);
    }

    public int GetNameIndex(int outletIndex)
    {
        int group = outletIndex / 8;
        int position = outletIndex % 8;
        return position * 3 + group;
    }

    private Task Poll(CancellationToken token) => Enqueue(() => _restClient.Get(_statusUri)).WaitAsync(token);

    public void TurnOnOutlet(ServerEdgeOutlet outlet) => _ = Enqueue(async () =>
    {
        if (!await ReadStatus("power on", outlet))
            return;
        if (await SendCommand("power on", $"outlet {outlet.Name}", Mask(_onUri, outlet, PowerState.On)))
            outlet.OverridePowerState(PowerState.On);
    });

    public void TurnOffOutlet(ServerEdgeOutlet outlet) => _ = Enqueue(async () =>
    {
        if (!await ReadStatus("power off", outlet))
            return;
        if (await SendCommand("power off", $"outlet {outlet.Name}", Mask(_offUri, outlet, PowerState.Off)))
            outlet.OverridePowerState(PowerState.Off);
    });

    public void RebootOutlet(ServerEdgeOutlet outlet) => _ = Enqueue(async () =>
    {
        if (!await ReadStatus("reboot", outlet))
            return;
        if (await SendCommand("reboot", $"outlet {outlet.Name}", Mask(_rebootUri, outlet, PowerState.Rebooting)))
            outlet.OverridePowerState(PowerState.Rebooting);
    });

    public override void PowerOn() => _ = Enqueue(async () =>
    {
        if (await SendCommand("power on", AllOutlets,
                new Uri(_onUri + new string('1', Outlets.Count), UriKind.Relative)))
            Outlets.ForEach(x => x.OverridePowerState(PowerState.On));
    });

    public override void PowerOff() => _ = Enqueue(async () =>
    {
        if (await SendCommand("power off", AllOutlets,
                new Uri(_offUri + new string('1', Outlets.Count), UriKind.Relative)))
            Outlets.ForEach(x => x.OverridePowerState(PowerState.Off));
    });

    private async Task<bool> ReadStatus(string command, ServerEdgeOutlet outlet)
    {
        _lastReply = NoReply;
        await _restClient.Get(_statusUri);
        if (CommunicationState == CommunicationState.Okay)
            return true;
        using (PushProperties(nameof(ReadStatus)))
            LogWarning("Not sending the {Command} command for outlet {Outlet}, the status page could not be read: {Reply}",
                command, outlet.Name, _lastReply);
        return false;
    }

    private async Task<bool> SendCommand(string command, string target, Uri mask)
    {
        _lastReply = NoReply;
        await _restClient.Get(mask);
        if (CommunicationState == CommunicationState.Okay)
            return true;
        using (PushProperties(nameof(SendCommand)))
            LogWarning("The {Command} command for {Target} failed: {Reply}", command, target, _lastReply);
        return false;
    }

    private Uri Mask(string path, ServerEdgeOutlet target, PowerState flaggedState)
    {
        StringBuilder sb = new StringBuilder(path);
        Outlets.ForEach(outlet => sb.Append(outlet == target || outlet.PowerState == flaggedState ? "1" : "0"));
        return new Uri(sb.ToString(), UriKind.Relative);
    }

    private Task Enqueue(Func<Task> request)
    {
        lock (_requestsLock)
            return _requests = _requests.ContinueWith(_ => SendLogged(request), TaskScheduler.Default).Unwrap();
    }

    private async Task SendLogged(Func<Task> request)
    {
        if (_disposed)
            return;
        try
        {
            await request();
        }
        catch (Exception e)
        {
            LogException(e, "A request to the PDU failed");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _restClient.HttpResponseHandlers -= HandleResponse;
        _restClient.ConnectionStateHandlers -= HandleConnectionState;
        _restClient.ConnectionStateHandlers -= HandleRequestedDisconnect;
        Task.Run(_pollWorker.Stop).GetAwaiter().GetResult();
        LogBaseRegistry.Deregister(_pollWorker);
        foreach (var outlet in Outlets)
            LogBaseRegistry.Deregister(outlet);
        LogBaseRegistry.Deregister(this);
        GC.SuppressFinalize(this);
    }
}