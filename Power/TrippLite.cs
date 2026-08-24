using AVCoders.CommunicationClients;
using AVCoders.Core;

namespace AVCoders.Power;

public enum TrippLiteInputSource
{
    Unknown,
    A,
    B
}

public record TrippLiteInputFeedStatus(TrippLiteInputSource Source, bool Available, float Voltage);

public delegate void TrippLiteInputSourceHandler(TrippLiteInputSource source);

public delegate void TrippLiteInputFeedHandler(IReadOnlyList<TrippLiteInputFeedStatus> feeds);

public class TrippLiteOutlet : Outlet
{
    public readonly int DeviceIndex;
    public readonly int OutletNumber;
    public bool Controllable { get; }
    private readonly TrippLitePdu _pdu;

    public TrippLiteOutlet(string name, TrippLitePdu pdu, int deviceIndex, int outletNumber, bool controllable)
        : base(name)
    {
        _pdu = pdu;
        DeviceIndex = deviceIndex;
        OutletNumber = outletNumber;
        Controllable = controllable;
    }

    public override void PowerOn() => _pdu.PowerOn(this);

    public override void PowerOff() => _pdu.PowerOff(this);

    public override void Reboot() => _pdu.Cycle(this);
}

public class TrippLitePdu : Pdu
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(10);

    public StringHandler? ModelHandlers;
    public TrippLiteInputSourceHandler? ActiveSourceHandlers;
    public TrippLiteInputFeedHandler? InputFeedHandlers;
    public FloatHandler? OutputVoltageHandlers;
    public IntHandler? OutputPowerHandlers;

    private const string DeviceCountOid = "1.3.6.1.4.1.850.1.1.1.1.0";
    private const string DeviceTypeOidPrefix = "1.3.6.1.4.1.850.1.1.1.2.1.3.";
    private const string DeviceModelOidPrefix = "1.3.6.1.4.1.850.1.1.1.2.1.5.";
    private const string AgentFirmwareVersionOid = "1.3.6.1.4.1.850.1.2.1.1.2.0";
    private const string AgentSerialNumberOid = "1.3.6.1.4.1.850.1.2.1.1.5.0";
    private const string PduBranch = "1.3.6.1.4.1.850.1.1.3.2";
    private const string AtsBranch = "1.3.6.1.4.1.850.1.1.3.4";

    private const int CommandOff = 1;
    private const int CommandOn = 2;
    private const int CommandCycle = 3;

    private const int SourceAOnly = 1;
    private const int SourceBOnly = 2;
    private const int BothSources = 3;

    private const string DiscoveryIssueKey = "unanswered-discovery";
    private const string OutletPollIssueKey = "unanswered-outlet-poll";
    private const string AtsPollIssueKey = "unanswered-ats-poll";
    private const string RedundancyIssueKey = "input-redundancy";
    private const string CommandIssueKey = "unacknowledged-command";

    private readonly AvCodersSnmpV3Client _client;
    private readonly ThreadWorker _pollWorker;
    private volatile IReadOnlyList<DiscoveredDevice> _devices = [];
    private volatile bool _initialised;
    private TrippLiteInputSource _activeSource = TrippLiteInputSource.Unknown;
    private IReadOnlyList<TrippLiteInputFeedStatus> _inputFeeds = [];
    private float _outputVoltage;
    private int _outputPowerWatts;

    private record DiscoveredDevice(int Index, string Root, bool IsAts, string Model,
        IReadOnlyDictionary<int, TrippLiteOutlet> OutletsByNumber);

    public TrippLitePdu(string name, AvCodersSnmpV3Client client, TimeSpan? pollInterval = null)
        : base(name, client)
    {
        _client = client;
        CommunicationState = CommunicationState.Unknown;
        _pollWorker = new ThreadWorker(Poll, pollInterval ?? DefaultPollInterval);
        _pollWorker.Restart();
    }

    public string Model { get; private set; } = string.Empty;

    public string SerialNumber { get; private set; } = string.Empty;

    public string FirmwareVersion { get; private set; } = string.Empty;

    public TrippLiteInputSource ActiveSource
    {
        get => _activeSource;
        private set
        {
            if (_activeSource == value)
                return;
            _activeSource = value;
            AddEvent(EventType.DriverState, $"Input source {value} is now active");
            ActiveSourceHandlers?.Invoke(value);
        }
    }

    public IReadOnlyList<TrippLiteInputFeedStatus> InputFeeds
    {
        get => _inputFeeds;
        private set
        {
            if (_inputFeeds.SequenceEqual(value))
                return;
            _inputFeeds = value;
            InputFeedHandlers?.Invoke(value);
        }
    }

    public float OutputVoltage
    {
        get => _outputVoltage;
        private set
        {
            if (Math.Abs(_outputVoltage - value) < 0.05f)
                return;
            _outputVoltage = value;
            OutputVoltageHandlers?.Invoke(value);
        }
    }

    public int OutputPowerWatts
    {
        get => _outputPowerWatts;
        private set
        {
            if (_outputPowerWatts == value)
                return;
            _outputPowerWatts = value;
            OutputPowerHandlers?.Invoke(value);
        }
    }

    public void Reinitialise() => _initialised = false;

    public override void PowerOn()
    {
        foreach (var outlet in ControllableOutlets())
            PowerOn(outlet);
    }

    public override void PowerOff()
    {
        foreach (var outlet in ControllableOutlets())
            PowerOff(outlet);
    }

    public void PowerOn(TrippLiteOutlet outlet)
    {
        if (SendOutletCommand(outlet, CommandOn, "power on"))
            outlet.OverridePowerState(PowerState.On);
    }

    public void PowerOff(TrippLiteOutlet outlet)
    {
        if (SendOutletCommand(outlet, CommandOff, "power off"))
            outlet.OverridePowerState(PowerState.Off);
    }

    public void Cycle(TrippLiteOutlet outlet)
    {
        if (SendOutletCommand(outlet, CommandCycle, "cycle"))
            outlet.OverridePowerState(PowerState.Rebooting);
    }

    private List<TrippLiteOutlet> ControllableOutlets() =>
        Outlets.OfType<TrippLiteOutlet>().Where(outlet => outlet.Controllable).ToList();

    private bool SendOutletCommand(TrippLiteOutlet outlet, int command, string description)
    {
        if (!outlet.Controllable)
        {
            using (PushProperties(nameof(SendOutletCommand)))
                LogWarning("Ignoring the {Command} command, outlet {Outlet} is not controllable", description,
                    outlet.Name);
            return false;
        }
        var response = _client.Set(
            $"{DeviceRoot(outlet.DeviceIndex)}.3.3.1.1.6.{outlet.DeviceIndex}.{outlet.OutletNumber}", command);
        if (response.Count == 0)
        {
            RaiseMomentaryIssue($"The {description} command for outlet {outlet.Name} was not acknowledged",
                key: CommandIssueKey);
            return false;
        }
        AddEvent(EventType.Power, $"Sent the {description} command to outlet {outlet.Name}");
        return true;
    }

    private string DeviceRoot(int deviceIndex) =>
        _devices.FirstOrDefault(device => device.Index == deviceIndex)?.Root ?? AtsBranch;

    private Task Poll(CancellationToken token)
    {
        if (!_initialised)
            Initialise();
        if (!_initialised)
            return Task.CompletedTask;

        var healthy = true;
        foreach (var device in _devices)
        {
            token.ThrowIfCancellationRequested();
            if (!PollOutletStates(device))
                healthy = false;
            if (device.IsAts && !PollAtsStatus(device))
                healthy = false;
        }
        CommunicationState = healthy ? CommunicationState.Okay : CommunicationState.Error;
        return Task.CompletedTask;
    }

    private void Initialise()
    {
        var deviceCount = GetNumber(DeviceCountOid);
        if (deviceCount == null)
        {
            ReportDiscoveryFailure("The device count query was not answered");
            return;
        }
        FirmwareVersion = GetText(AgentFirmwareVersionOid) ?? string.Empty;
        SerialNumber = GetText(AgentSerialNumberOid) ?? string.Empty;

        List<DiscoveredDevice> devices = [];
        for (var deviceIndex = 1; deviceIndex <= deviceCount; deviceIndex++)
        {
            var deviceType = GetText(DeviceTypeOidPrefix + deviceIndex)?.TrimStart('.');
            var model = GetText(DeviceModelOidPrefix + deviceIndex) ?? "Unknown";
            if (deviceType is not (PduBranch or AtsBranch))
            {
                AddEvent(EventType.DriverState,
                    $"Skipping device {deviceIndex} ({model}), type {deviceType} is not a PDU or ATS");
                continue;
            }
            var outletCount = GetNumber($"{deviceType}.1.2.1.4.{deviceIndex}");
            if (outletCount == null)
            {
                ReportDiscoveryFailure($"The outlet count query for device {deviceIndex} was not answered");
                return;
            }
            var names = WalkColumn($"{deviceType}.3.3.1.1.2.{deviceIndex}");
            var controllables = WalkColumn($"{deviceType}.3.3.1.1.5.{deviceIndex}");
            if (names.Count == 0)
            {
                ReportDiscoveryFailure($"The outlet name walk for device {deviceIndex} was not answered");
                return;
            }
            Dictionary<int, TrippLiteOutlet> outlets = [];
            for (var outletNumber = 1; outletNumber <= outletCount; outletNumber++)
            {
                var name = names.GetValueOrDefault(outletNumber.ToString());
                var controllable = controllables.GetValueOrDefault(outletNumber.ToString()) == "1";
                outlets[outletNumber] = new TrippLiteOutlet(
                    string.IsNullOrWhiteSpace(name) ? $"Outlet {outletNumber}" : name,
                    this, deviceIndex, outletNumber, controllable);
            }
            devices.Add(new DiscoveredDevice(deviceIndex, deviceType, deviceType == AtsBranch, model, outlets));
        }

        _devices = devices;
        ClearOutlets();
        foreach (var outlet in devices.SelectMany(device => device.OutletsByNumber.OrderBy(pair => pair.Key)))
            AddOutlet(outlet.Value);
        var combinedModel = string.Join(", ", devices.Select(device => device.Model));
        if (Model != combinedModel)
        {
            Model = combinedModel;
            ModelHandlers?.Invoke(combinedModel);
        }
        ResolveIssue(DiscoveryIssueKey);
        _initialised = true;
        AddEvent(EventType.Connection,
            $"Connected to {Model} (serial {SerialNumber}, firmware {FirmwareVersion}), {Outlets.Count} outlets discovered");
        OutletDefinitionHandlers?.Invoke(Outlets);
        CommunicationState = CommunicationState.Okay;
    }

    private void ReportDiscoveryFailure(string message)
    {
        RaiseMomentaryIssue(message, key: DiscoveryIssueKey, escalateAfter: 3);
        CommunicationState = CommunicationState.Error;
    }

    private bool PollOutletStates(DiscoveredDevice device)
    {
        var states = WalkColumn($"{device.Root}.3.3.1.1.4.{device.Index}");
        if (states.Count == 0)
        {
            RaiseMomentaryIssue($"The outlet state poll for device {device.Index} was not answered",
                key: OutletPollIssueKey, escalateAfter: 3);
            return false;
        }
        foreach (var (outletNumber, outlet) in device.OutletsByNumber)
        {
            outlet.OverridePowerState(states.GetValueOrDefault(outletNumber.ToString()) switch
            {
                "1" => PowerState.Off,
                "2" => PowerState.On,
                _ => PowerState.Unknown
            });
        }
        ResolveIssue(OutletPollIssueKey);
        return true;
    }

    private bool PollAtsStatus(DiscoveredDevice device)
    {
        var availability = GetNumber($"{device.Root}.3.1.1.1.12.{device.Index}");
        if (availability == null)
        {
            RaiseMomentaryIssue($"The input source poll for device {device.Index} was not answered",
                key: AtsPollIssueKey, escalateAfter: 3);
            return false;
        }
        ResolveIssue(AtsPollIssueKey);

        switch (availability)
        {
            case BothSources:
                ResolveIssue(RedundancyIssueKey);
                break;
            case SourceAOnly:
                RaiseOngoingIssue(RedundancyIssueKey, "Input source B has failed, power redundancy is lost");
                break;
            case SourceBOnly:
                RaiseOngoingIssue(RedundancyIssueKey, "Input source A has failed, power redundancy is lost");
                break;
        }

        var voltages = WalkColumn($"{device.Root}.3.1.2.1.5.{device.Index}");
        InputFeeds = new List<TrippLiteInputFeedStatus>
        {
            BuildFeedStatus(TrippLiteInputSource.A, availability.Value is SourceAOnly or BothSources, voltages, 1),
            BuildFeedStatus(TrippLiteInputSource.B, availability.Value is SourceBOnly or BothSources, voltages, 2)
        };

        var inUse = GetNumber($"{device.Root}.3.1.1.1.13.{device.Index}");
        ActiveSource = inUse switch
        {
            0 => TrippLiteInputSource.A,
            1 => TrippLiteInputSource.B,
            _ => availability switch
            {
                SourceAOnly => TrippLiteInputSource.A,
                SourceBOnly => TrippLiteInputSource.B,
                _ => ActiveSource
            }
        };

        var outputVoltage = GetNumber($"{device.Root}.3.2.1.1.4.{device.Index}.1");
        if (outputVoltage != null)
            OutputVoltage = outputVoltage.Value / 10f;
        var outputPower = GetNumber($"{device.Root}.2.1.1.9.{device.Index}");
        if (outputPower != null)
            OutputPowerWatts = outputPower.Value;
        return true;
    }

    private static TrippLiteInputFeedStatus BuildFeedStatus(TrippLiteInputSource source, bool available,
        Dictionary<string, string> voltages, int inputNumber)
    {
        var raw = voltages.FirstOrDefault(pair => pair.Key.StartsWith($"{inputNumber}.")).Value;
        var voltage = int.TryParse(raw, out var tenths) ? tenths / 10f : 0f;
        return new TrippLiteInputFeedStatus(source, available, voltage);
    }

    private int? GetNumber(string oid)
    {
        var response = _client.Get(oid);
        if (response.Count == 0 || !int.TryParse(response[0].Data.ToString(), out var value))
            return null;
        return value;
    }

    private string? GetText(string oid)
    {
        var response = _client.Get(oid);
        return response.Count == 0 ? null : response[0].Data.ToString();
    }

    private Dictionary<string, string> WalkColumn(string columnOid)
    {
        Dictionary<string, string> results = [];
        var prefix = columnOid.TrimStart('.') + ".";
        foreach (var variable in _client.Walk(columnOid))
        {
            var id = variable.Id.ToString();
            if (id.StartsWith(prefix))
                results[id[prefix.Length..]] = variable.Data.ToString();
        }
        return results;
    }
}
