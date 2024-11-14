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

    public override void PowerOn()
    {
        CurrentPowerState = PowerState.On;
        _pdu.TurnOnOutlet();
    }

    public override void PowerOff()
    {
        CurrentPowerState = PowerState.Off;
        _pdu.TurnOffOutlet();
    }
}

public class ServerEdgePdu: Pdu
{
    public const string DefaultUser = "snmp";
    public const string DefaultPassword = "1234";
    private readonly RestComms _restClient;
    private readonly Uri _getNamesUri;
    private readonly Uri _statusUri;
    private readonly Uri _onUri;
    private readonly Uri _offUri;
    private readonly Uri _setNameUri;
    private readonly ThreadWorker _pollWorker;

    public ServerEdgePdu(RestComms restClient, string name, string username, string password, int numberOfOutlets) : base(name)
    {
        _restClient = restClient;
        string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
        _restClient.AddDefaultHeader("Authorization", $"Basic {credentials}");
        _restClient.HttpResponseHandlers += HandleResponse;
        
        _getNamesUri = new Uri("/Getname.xml", UriKind.Relative);
        _statusUri = new Uri("/status.xml", UriKind.Relative);
        _onUri = new Uri("/ons.cgi?led=", UriKind.Relative);
        _offUri = new Uri("/offs.cgi?led=", UriKind.Relative);
        _setNameUri = new Uri("names1.cgi?led=0,", UriKind.Relative);

        for (int i = 0; i < numberOfOutlets; i++)
        {
            Outlets.Add(new ServerEdgeOutlet("Unknown", this));
        }

        _restClient.Get(_getNamesUri);

        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(30));
        _pollWorker.Restart();

    }

    private void HandleResponse(HttpResponseMessage response)
    {
        string responseString = response.Content.ReadAsStringAsync().Result;
        XmlDocument xmlDoc = new XmlDocument();
        xmlDoc.LoadXml(responseString);
        string namePayload = String.Empty;

        foreach (XmlNode childNode in xmlDoc.DocumentElement!.ChildNodes)
        {
            if (childNode.Name.StartsWith("pot"))
            {
                
            }
            else if (childNode.Name.StartsWith("na"))
            {
                namePayload += childNode.InnerText;
            }
        }
        
        if(namePayload != String.Empty)
            ProcessOutletNames(namePayload);
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

    private Task Poll(CancellationToken arg)
    {
        _restClient.Get(_statusUri);
        return Task.CompletedTask;
    }

    public void TurnOnOutlet()
    {
        StringBuilder sb = new StringBuilder();
        Outlets.ForEach(outlet => { sb.Append(outlet.CurrentPowerState == PowerState.On ? "1" : "0"); });
        Uri powerOnUri = new Uri(_onUri, sb.ToString());
        _restClient.Get(powerOnUri);
    }

    public void TurnOffOutlet()
    {
        StringBuilder sb = new StringBuilder();
        Outlets.ForEach(outlet => { sb.Append(outlet.CurrentPowerState == PowerState.On ? "1" : "0"); });
        Uri powerOffUri = new Uri(_offUri, sb.ToString());
        _restClient.Get(powerOffUri);
    }

    public override void PowerOn()
    {
        Outlets.ForEach(x => x.CurrentPowerState = PowerState.On);
        TurnOnOutlet();
    }

    public override void PowerOff()
    {
        Outlets.ForEach(x => x.CurrentPowerState = PowerState.Off);
        TurnOffOutlet();
    }
}