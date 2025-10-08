using System.Net;
using AVCoders.CommunicationClients;
using AVCoders.Core;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using Serilog;

namespace AVCoders.Power;

public class EatonOutlet : Outlet
{
    public readonly int OutletNumber;
    public readonly int DeviceIndex;
    private readonly EatonPdu _pdu;
    private readonly ThreadWorker _pollWorker;
    
    public EatonOutlet(string name, EatonPdu pdu, int outletNumber, int deviceIndex) : base(name)
    {
        _pdu = pdu;
        OutletNumber = outletNumber;
        DeviceIndex = deviceIndex;
        _pollWorker = new ThreadWorker(PollPowerState, TimeSpan.FromSeconds(12));
        _pollWorker.Restart();
    }

    private Task PollPowerState(CancellationToken arg)
    {
        var currentState = _pdu.GetPowerState(this);
        if(PowerState != currentState)
            AddEvent(EventType.Power, currentState.ToString());
        PowerState = currentState;
        return Task.CompletedTask;   
    }

    public override void PowerOn()
    {
        _pdu.PowerOn(this);
        Thread.Sleep(1000);
        PowerState = _pdu.GetPowerState(this);
        AddEvent(EventType.Power, nameof(PowerState.On));
    }

    public override void PowerOff()
    {
        _pdu.PowerOff(this);
        Thread.Sleep(1000);
        PowerState = _pdu.GetPowerState(this);
        AddEvent(EventType.Power, nameof(PowerState.Off));
    }

    public override void Reboot()
    {
        _pdu.Cycle(this);
        Thread.Sleep(1000);
        PowerState = _pdu.GetPowerState(this);
        Thread.Sleep(5000);
        PowerState = _pdu.GetPowerState(this);
    }
}

public class EatonPdu : Pdu
{
    private readonly AvCodersSnmpV3Client _client;
    private ThreadWorker _waitForConnectionWorker;

    public EatonPdu(string name, AvCodersSnmpV3Client client) : base(name, client)
    {
        _client = client;
        _waitForConnectionWorker = new ThreadWorker(Initialise, TimeSpan.FromSeconds(12));
        _waitForConnectionWorker.Restart();
        CommunicationState = CommunicationState.Unknown;
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
        _client.Set($".1.3.6.1.4.1.850.1.1.3.2.3.3.1.1.6.{outlet.DeviceIndex}.{outlet.OutletNumber}", "2");
        AddEvent(EventType.Power, $"Outlet {outlet.Name} turned on");
    }

    public void PowerOff(EatonOutlet outlet)
    {
        _client.Set($".1.3.6.1.4.1.850.1.1.3.2.3.3.1.1.6.{outlet.DeviceIndex}.{outlet.OutletNumber}", "1");
        AddEvent(EventType.Power, $"Outlet {outlet.Name} turned off");   
    }

    public void Cycle(EatonOutlet outlet)
    {
        _client.Set($".1.3.6.1.4.1.850.1.1.3.2.3.3.1.1.6.{outlet.DeviceIndex}.{outlet.OutletNumber}", "3");
        AddEvent(EventType.Power, $"Outlet {outlet.Name} cycled");  
    }
    
    public PowerState GetPowerState(EatonOutlet outlet)
    {
        var response = _client.Get($".1.3.6.1.4.1.850.1.1.3.2.3.3.1.1.4.{outlet.DeviceIndex}.{outlet.OutletNumber}");
        return response[0].Data.ToString() switch
        {
            "1" => PowerState.Off,
            "2" => PowerState.On,
            _ => PowerState.Unknown
        }; 
    }
}