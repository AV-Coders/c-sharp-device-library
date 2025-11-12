using AVCoders.CommunicationClients;
using AVCoders.Core;

namespace AVCoders.Power;

public class EatonOutlet : Outlet
{
    public readonly int OutletNumber;
    public readonly int DeviceIndex;
    private readonly EatonPdu _pdu;
    
    public EatonOutlet(string name, EatonPdu pdu, int outletNumber, int deviceIndex) : base(name)
    {
        _pdu = pdu;
        OutletNumber = outletNumber;
        DeviceIndex = deviceIndex;
    }

    public void PollPowerState()
    {
        var currentState = _pdu.GetPowerState(this);
        PowerState = currentState;   
    }

    public override void PowerOn()
    {
        _pdu.PowerOn(this);
        Thread.Sleep(5000);
        PowerState = _pdu.GetPowerState(this);
    }

    public override void PowerOff()
    {
        _pdu.PowerOff(this);
        Thread.Sleep(5000);
        PowerState = _pdu.GetPowerState(this);
    }

    public override void Reboot()
    {
        _pdu.Cycle(this);
        Thread.Sleep(5000);
        PowerState = _pdu.GetPowerState(this);
        Thread.Sleep(5000);
        PowerState = _pdu.GetPowerState(this);
    }
}

public class EatonPdu : Pdu
{
    private readonly AvCodersSnmpV3Client _client;
    private ThreadWorker _waitForConnectionWorker;
    private ThreadWorker _pollWorker;
    private readonly string _setPowerOid = ".1.3.6.1.4.1.850.1.1.3.4.3.3.1.1.6.";

    public EatonPdu(string name, AvCodersSnmpV3Client client) : base(name, client)
    {
        _client = client;
        _waitForConnectionWorker = new ThreadWorker(Initialise, TimeSpan.FromSeconds(12));
        _waitForConnectionWorker.Restart();
        
        _pollWorker =  new ThreadWorker(Poll, TimeSpan.FromSeconds(12), true);
        _pollWorker.Restart();
        CommunicationState = CommunicationState.Unknown;
    }

    private Task Poll(CancellationToken token)
    {
        Outlets.ForEach(x =>
        {
            Task.Delay(TimeSpan.FromMilliseconds(333), token).Wait(token);
            var outlet = x as EatonOutlet;
            outlet!.PollPowerState();
        });
        return Task.CompletedTask;
    }

    private Task Initialise(CancellationToken arg)
    {
        var agent = _client.Get(".1.3.6.1.4.1.850.1.2.1.1.1.0");
        if (agent.Count == 0)
        {
            AddEvent(EventType.Error, "Could not find Eaton PDU");
            CommunicationState = CommunicationState.Error;
            return Task.CompletedTask;
        }

        AddEvent(EventType.Connection, "Connected to Eaton PDU, creating outlets");
        ClearOutlets();
        for (int i = 1; i < 9; i++)
        {
            string name = _client.Get($".1.3.6.1.4.1.850.1.1.3.4.3.3.1.1.2.1.{i}")[0].Data.ToString();
            AddOutlet(new EatonOutlet(name, this, i, 1));
            AddEvent(EventType.DriverState, $"Outlet {name} created");
        }
        _waitForConnectionWorker.Stop();
        OutletDefinitionHandlers?.Invoke(Outlets);
        CommunicationState = CommunicationState.Okay;
        return Task.CompletedTask;
    }

    public override void PowerOn()
    {
        foreach (Outlet outlet in Outlets)
        {
            PowerOn(outlet as EatonOutlet);
        }
    }

    public override void PowerOff()
    {
        foreach (Outlet outlet in Outlets)
        {
            PowerOff(outlet as EatonOutlet);
        }
    }

    public void PowerOn(EatonOutlet outlet)
    {
        _client.Set($"{_setPowerOid}{outlet.DeviceIndex}.{outlet.OutletNumber}", 2);
        AddEvent(EventType.Power, $"Outlet {outlet.Name} turned on");
    }

    public void PowerOff(EatonOutlet outlet)
    {
        _client.Set($"{_setPowerOid}{outlet.DeviceIndex}.{outlet.OutletNumber}", 1);
        AddEvent(EventType.Power, $"Outlet {outlet.Name} turned off");   
    }

    public void Cycle(EatonOutlet outlet)
    {
        _client.Set($"{_setPowerOid}{outlet.DeviceIndex}.{outlet.OutletNumber}", 3);
        AddEvent(EventType.Power, $"Outlet {outlet.Name} cycled");  
    }
    
    public PowerState GetPowerState(EatonOutlet outlet)
    {
        var response = _client.Get($".1.3.6.1.4.1.850.1.1.3.4.3.3.1.1.4.{outlet.DeviceIndex}.{outlet.OutletNumber}");
        return response[0].Data.ToString() switch
        {
            "1" => PowerState.Off,
            "2" => PowerState.On,
            _ => PowerState.Unknown
        }; 
    }
}