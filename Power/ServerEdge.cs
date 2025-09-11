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
        PowerState = PowerState.On;
        _pdu.TurnOnOutlet();
    }

    public override void PowerOff()
    {
        PowerState = PowerState.Off;
        _pdu.TurnOffOutlet();
    }

    public override void Reboot()
    {
        PowerState = PowerState.Rebooting;
        _pdu.RebootOutlet();
    }
}

public class ServerEdgePdu: Pdu
{
    public const string DefaultUser = "snmp";
    public const string DefaultPassword = "1234";
    private readonly RestComms _restClient;
    private readonly Uri _getNamesUri;
    private readonly Uri _statusUri;
    private readonly string _onUri = "/ons.cgi?led=";
    private readonly string _offUri = "/offs.cgi?led=";
    private readonly string _rebootUri = "/offon.cgi?led=";
    private readonly Uri _setNameUri;
    private readonly ThreadWorker _pollWorker;

    public ServerEdgePdu(RestComms restClient, string name, string username, string password, int numberOfOutlets) 
        : base(name, restClient, CommandStringFormat.Ascii)
    {
        _restClient = restClient;
        string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
        _restClient.AddDefaultHeader("Authorization", $"Basic {credentials}");
        _restClient.HttpResponseHandlers += HandleResponse;
        
        _getNamesUri = new Uri("/Getname.xml", UriKind.Relative);
        _statusUri = new Uri("/status.xml", UriKind.Relative);
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
        string namePayload = string.Empty;

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
        
        if(namePayload != string.Empty)
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
        StringBuilder sb = new StringBuilder(_onUri);
        Outlets.ForEach(outlet => { sb.Append(outlet.PowerState == PowerState.On ? "1" : "0"); });
        Uri powerOnUri = new Uri(sb.ToString(), UriKind.Relative);
        _restClient.Get(powerOnUri);
    }

    public void TurnOffOutlet()
    {
        StringBuilder sb = new StringBuilder(_offUri);
        Outlets.ForEach(outlet => { sb.Append(outlet.PowerState == PowerState.Off ? "1" : "0"); });
        Uri powerOffUri = new Uri(sb.ToString(), UriKind.Relative);
        _restClient.Get(powerOffUri);
    }
    
    public void RebootOutlet()
    {
        StringBuilder sb = new StringBuilder(_rebootUri);
        Outlets.ForEach(outlet =>
        {
            sb.Append(outlet.PowerState == PowerState.Rebooting ? "1" : "0");
            new Thread(_ =>
            {
                Thread.Sleep(2000);
                outlet.OverridePowerState(PowerState.On);
            }).Start();
            
        });
        Uri rebootUri = new Uri(sb.ToString(), UriKind.Relative);
        _restClient.Get(rebootUri);
    }

    public override void PowerOn()
    {
        Outlets.ForEach(x => x.OverridePowerState(PowerState.On));
        TurnOnOutlet();
    }

    public override void PowerOff()
    {
        Outlets.ForEach(x => x.OverridePowerState(PowerState.Off));
        TurnOffOutlet();
    }
}