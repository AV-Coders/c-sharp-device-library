using System.Text.RegularExpressions;
using AVCoders.CommunicationClients;
using AVCoders.Core;
using Lextm.SharpSnmpLib;

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

public record TrippLiteSensorReading(string Name, string Model, float? TemperatureCelsius, int? HumidityPercent,
    bool InAlarm);

public delegate void TrippLiteSensorHandler(IReadOnlyList<TrippLiteSensorReading> readings);

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
    public TrippLiteSensorHandler? SensorHandlers;

    private const string DeviceCountOid = "1.3.6.1.4.1.850.1.1.1.1.0";
    private const string DeviceTypeOidPrefix = "1.3.6.1.4.1.850.1.1.1.2.1.3.";
    private const string DeviceModelOidPrefix = "1.3.6.1.4.1.850.1.1.1.2.1.5.";
    private const string DeviceNameOidPrefix = "1.3.6.1.4.1.850.1.1.1.2.1.6.";
    private const string AgentFirmwareVersionOid = "1.3.6.1.4.1.850.1.2.1.1.2.0";
    private const string AgentSerialNumberOid = "1.3.6.1.4.1.850.1.2.1.1.5.0";
    private const string AgentMacOid = "1.3.6.1.4.1.850.1.2.1.1.4.0";
    private const string AlarmsPresentOid = "1.3.6.1.4.1.850.1.3.1.0";
    private const string AlarmDetailColumn = "1.3.6.1.4.1.850.1.3.2.1.6";
    private const string PduBranch = "1.3.6.1.4.1.850.1.1.3.2";
    private const string EnvirosenseBranch = "1.3.6.1.4.1.850.1.1.3.3";
    private const string AtsBranch = "1.3.6.1.4.1.850.1.1.3.4";

    private const int TruthTrue = 1;

    private const int CommandOff = 1;
    private const int CommandOn = 2;
    private const int CommandCycle = 3;

    private const int SourceAOnly = 1;
    private const int SourceBOnly = 2;
    private const int BothSources = 3;

    private const string DiscoveryIssueKey = "unanswered-discovery";
    private const string OutletPollIssueKey = "unanswered-outlet-poll";
    private const string AtsPollIssueKey = "unanswered-ats-poll";
    private const string SensorPollIssueKey = "unanswered-sensor-poll";
    private const string RedundancyIssueKey = "input-redundancy";
    private const string CommandIssueKey = "unacknowledged-command";
    private const string FirmwareIssueKey = "firmware-read";
    private const string SerialIssueKey = "serial-read";
    private const string MacIssueKey = "mac-read";
    private const string AlarmCountIssueKey = "alarm-count-read";
    private const string AlarmsIssueKey = "alarms-present";

    private const string ModelLabel = "Model";
    private const string SerialLabel = "Serial";
    private const string FirmwareLabel = "Firmware";
    private const string MacLabel = "MAC";
    private const string AlarmsLabel = "Alarms";
    private const string OutputVoltageLabel = "Output Voltage";
    private const string OutputPowerLabel = "Output Power";
    private const string NoAnswer = "no answer";
    private const string NoReply = "no reply";
    private const string NotAnswered = "was not answered";
    private const string NotAcknowledged = "was not acknowledged";

    private const int MaxMissedReplies = 3;

    private static readonly SnmpRead FirmwareRead = new("firmware", AgentFirmwareVersionOid, FirmwareLabel, FirmwareIssueKey);
    private static readonly SnmpRead SerialRead = new("serial number", AgentSerialNumberOid, SerialLabel, SerialIssueKey);
    private static readonly SnmpRead MacRead = new("MAC", AgentMacOid, MacLabel, MacIssueKey);
    private static readonly SnmpRead[] IdentityReads = [FirmwareRead, SerialRead, MacRead];
    private static readonly SnmpRead AlarmCountRead = new("alarm count", AlarmsPresentOid, AlarmsLabel, AlarmCountIssueKey);

    private static readonly Regex LoadOffAlarm = new(@"\bLoad(?: \d+)? Off\b", RegexOptions.IgnoreCase);
    private static readonly Regex InputAAlarm = new(@"\b(?:Input|Source) (?:A|1)\b", RegexOptions.IgnoreCase);
    private static readonly Regex InputBAlarm = new(@"\b(?:Input|Source) (?:B|2)\b", RegexOptions.IgnoreCase);

    private readonly AvCodersSnmpV3Client _client;
    private readonly HashSet<SnmpRead> _unreadIdentity = [];
    private readonly Dictionary<SnmpRead, int> _missedReplies = new();
    private readonly ThreadWorker _pollWorker;
    private volatile IReadOnlyList<DiscoveredDevice> _devices = [];
    private volatile IReadOnlyList<DiscoveredSensor> _sensors = [];
    private volatile bool _initialised;
    private volatile bool _inputAWarningDisabled;
    private volatile bool _inputBWarningDisabled;
    private volatile bool _alarmCountUnsupported;
    private string _lastQueryFailure = NotAnswered;
    private TrippLiteInputSource _activeSource = TrippLiteInputSource.Unknown;
    private volatile IReadOnlyList<TrippLiteInputFeedStatus> _inputFeeds = [];
    private volatile IReadOnlyList<TrippLiteSensorReading> _sensorReadings = [];
    private float _outputVoltage;
    private int _outputPowerWatts;

    private record DiscoveredDevice(int Index, string Root, bool IsAts, string Model,
        IReadOnlyDictionary<int, TrippLiteOutlet> OutletsByNumber);

    private record DiscoveredSensor(int Index, string Name, string Model, bool HasTemperature, bool HasHumidity);

    private record SnmpRead(string Name, string Oid, string Label, string IssueKey);

    private enum ReplyKind
    {
        Missed,
        NotSupported,
        Answered
    }

    public TrippLitePdu(string name, AvCodersSnmpV3Client client, TimeSpan? pollInterval = null)
        : this(name, client, pollInterval, null)
    {
    }

    internal TrippLitePdu(string name, AvCodersSnmpV3Client client, TimeSpan? pollInterval, TimeProvider? timeProvider)
        : base(name, client)
    {
        _client = client;
        CommunicationState = CommunicationState.Unknown;
        _pollWorker = new ThreadWorker(Poll, pollInterval ?? DefaultPollInterval, false, timeProvider);
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

    public IReadOnlyList<TrippLiteSensorReading> SensorReadings
    {
        get => _sensorReadings;
        private set
        {
            if (_sensorReadings.SequenceEqual(value))
                return;
            _sensorReadings = value;
            SensorHandlers?.Invoke(value);
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

    public void DisableInputWarning(TrippLiteInputSource source) => SetInputWarningDisabled(source, true);

    public void EnableInputWarning(TrippLiteInputSource source) => SetInputWarningDisabled(source, false);

    private void SetInputWarningDisabled(TrippLiteInputSource source, bool disabled)
    {
        var wasDisabled = source switch
        {
            TrippLiteInputSource.A => _inputAWarningDisabled,
            TrippLiteInputSource.B => _inputBWarningDisabled,
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Only inputs A and B have warnings")
        };
        if (wasDisabled == disabled)
            return;
        if (source == TrippLiteInputSource.A)
            _inputAWarningDisabled = disabled;
        else
            _inputBWarningDisabled = disabled;
        AddEvent(EventType.DriverState,
            $"Input source {source} failure warnings are {(disabled ? "disabled" : "enabled")}");
        var label = $"Input {source} Warning";
        if (disabled)
            SetDetail(label, "Disabled", DetailTone.Warning);
        else
            RemoveDetail(label);
    }

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
        var device = _devices.FirstOrDefault(discovered => discovered.Index == outlet.DeviceIndex);
        if (device == null)
        {
            using (PushProperties(nameof(SendOutletCommand)))
                LogWarning("Ignoring the {Command} command, outlet {Outlet} is on device {DeviceIndex}, which was not discovered",
                    description, outlet.Name, outlet.DeviceIndex);
            return false;
        }
        var response = _client.Set(
            $"{device.Root}.3.3.1.1.6.{outlet.DeviceIndex}.{outlet.OutletNumber}", command);
        if (response.Variables.Count == 0)
        {
            RaiseMomentaryIssue($"The {description} command for outlet {outlet.Name} {Failure(response, NotAcknowledged)}",
                key: CommandIssueKey);
            return false;
        }
        AddEvent(EventType.Power, $"Sent the {description} command to outlet {outlet.Name}");
        return true;
    }

    private Task Poll(CancellationToken token)
    {
        using (PushProperties("Poll"))
        {
            var wasInitialised = _initialised;
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
            if (!PollSensors(token))
                healthy = false;
            CommunicationState = healthy ? CommunicationState.Okay : CommunicationState.Error;
            if (!healthy)
                return Task.CompletedTask;
            PollAlarms();
            if (wasInitialised)
                ReadIdentity();
            return Task.CompletedTask;
        }
    }

    private void Initialise()
    {
        var deviceCount = GetNumber(DeviceCountOid);
        if (deviceCount == null)
        {
            ReportDiscoveryFailure($"The device count query {_lastQueryFailure}");
            return;
        }
        _unreadIdentity.UnionWith(IdentityReads);
        ReadIdentity();

        List<DiscoveredDevice> devices = [];
        List<DiscoveredSensor> sensors = [];
        for (var deviceIndex = 1; deviceIndex <= deviceCount; deviceIndex++)
        {
            var deviceType = GetText(DeviceTypeOidPrefix + deviceIndex)?.TrimStart('.');
            var model = GetText(DeviceModelOidPrefix + deviceIndex) ?? "Unknown";
            if (deviceType == EnvirosenseBranch)
            {
                var sensorName = GetText(DeviceNameOidPrefix + deviceIndex);
                var hasTemperature = GetNumber($"{EnvirosenseBranch}.1.2.1.1.{deviceIndex}");
                var hasHumidity = GetNumber($"{EnvirosenseBranch}.1.2.1.2.{deviceIndex}");
                if (hasTemperature == null || hasHumidity == null)
                {
                    ReportDiscoveryFailure($"The sensor capability query for device {deviceIndex} {_lastQueryFailure}");
                    return;
                }
                sensors.Add(new DiscoveredSensor(deviceIndex,
                    string.IsNullOrWhiteSpace(sensorName) ? $"Sensor {deviceIndex}" : sensorName, model,
                    hasTemperature == TruthTrue, hasHumidity == TruthTrue));
                continue;
            }
            if (deviceType is not (PduBranch or AtsBranch))
            {
                AddEvent(EventType.DriverState,
                    $"Skipping device {deviceIndex} ({model}), type {deviceType} is not a PDU, ATS or sensor");
                continue;
            }
            var outletCount = GetNumber($"{deviceType}.1.2.1.4.{deviceIndex}");
            if (outletCount == null)
            {
                ReportDiscoveryFailure($"The outlet count query for device {deviceIndex} {_lastQueryFailure}");
                return;
            }
            var names = WalkColumn($"{deviceType}.3.3.1.1.2.{deviceIndex}");
            var controllables = WalkColumn($"{deviceType}.3.3.1.1.5.{deviceIndex}");
            if (names.Count == 0)
            {
                ReportDiscoveryFailure($"The outlet name walk for device {deviceIndex} {_lastQueryFailure}");
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

        foreach (var previous in _sensors)
        {
            if (sensors.All(sensor => sensor.Index != previous.Index))
                ResolveIssue(SensorAlarmKey(previous.Index));
            if (sensors.All(sensor => sensor.Name != previous.Name))
                RemoveDetail(previous.Name);
        }
        _devices = devices;
        _sensors = sensors;
        foreach (var previous in Outlets)
            LogBaseRegistry.Deregister(previous);
        SetOutlets(devices.SelectMany(device => device.OutletsByNumber.OrderBy(pair => pair.Key))
            .Select(pair => (Outlet)pair.Value).ToList());
        var combinedModel = string.Join(", ", devices.Select(device => device.Model));
        SetDetail(ModelLabel, combinedModel);
        if (Model != combinedModel)
        {
            Model = combinedModel;
            ModelHandlers?.Invoke(combinedModel);
        }
        ResolveIssue(DiscoveryIssueKey);
        _alarmCountUnsupported = false;
        _initialised = true;
        AddEvent(EventType.Connection,
            $"Connected to {Model} (serial {SerialNumber}, firmware {FirmwareVersion}), {Outlets.Count} outlets and {sensors.Count} sensors discovered");
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
            RaiseMomentaryIssue($"The outlet state poll for device {device.Index} {_lastQueryFailure}",
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
            RaiseMomentaryIssue($"The input source poll for device {device.Index} {_lastQueryFailure}",
                key: AtsPollIssueKey, escalateAfter: 3);
            return false;
        }
        ResolveIssue(AtsPollIssueKey);

        switch (availability)
        {
            case BothSources:
            case SourceAOnly when _inputBWarningDisabled:
            case SourceBOnly when _inputAWarningDisabled:
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
            ShowInputFeed(TrippLiteInputSource.A, availability.Value is SourceAOnly or BothSources, voltages, 1),
            ShowInputFeed(TrippLiteInputSource.B, availability.Value is SourceBOnly or BothSources, voltages, 2)
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
        {
            SetDetail(OutputVoltageLabel, FormattableString.Invariant($"{outputVoltage.Value / 10f:0.0} V"));
            OutputVoltage = outputVoltage.Value / 10f;
        }
        var outputPower = GetNumber($"{device.Root}.2.1.1.9.{device.Index}");
        if (outputPower != null)
        {
            SetDetail(OutputPowerLabel, $"{outputPower.Value} W");
            OutputPowerWatts = outputPower.Value;
        }
        return true;
    }

    private bool PollSensors(CancellationToken token)
    {
        List<(DiscoveredSensor Sensor, TrippLiteSensorReading Reading)> polled = [];
        foreach (var sensor in _sensors)
        {
            token.ThrowIfCancellationRequested();
            var reading = PollSensor(sensor);
            if (reading == null)
            {
                RaiseMomentaryIssue($"The sensor poll for {sensor.Name} {_lastQueryFailure}",
                    key: SensorPollIssueKey, escalateAfter: 3);
                return false;
            }
            polled.Add((sensor, reading));
        }
        foreach (var (sensor, reading) in polled)
        {
            SetDetail(sensor.Name, SensorText(reading), reading.InAlarm ? DetailTone.Warning : DetailTone.Normal);
            if (reading.InAlarm)
                RaiseOngoingIssue(SensorAlarmKey(sensor.Index),
                    $"Environment sensor {sensor.Name} is reporting readings beyond its configured limits");
            else
                ResolveIssue(SensorAlarmKey(sensor.Index));
        }
        ResolveIssue(SensorPollIssueKey);
        SensorReadings = polled.Select(entry => entry.Reading).ToList();
        return true;
    }

    private static string SensorAlarmKey(int deviceIndex) => $"sensor-alarm-{deviceIndex}";

    private TrippLiteSensorReading? PollSensor(DiscoveredSensor sensor)
    {
        float? temperature = null;
        int? humidity = null;
        var inAlarm = false;
        if (sensor.HasTemperature)
        {
            var tenths = GetNumber($"{EnvirosenseBranch}.3.1.1.1.{sensor.Index}");
            var alarm = GetNumber($"{EnvirosenseBranch}.3.1.1.3.{sensor.Index}");
            if (tenths == null || alarm == null)
                return null;
            temperature = tenths.Value / 10f;
            inAlarm |= alarm == TruthTrue;
        }
        if (sensor.HasHumidity)
        {
            var percent = GetNumber($"{EnvirosenseBranch}.3.2.1.1.{sensor.Index}");
            var alarm = GetNumber($"{EnvirosenseBranch}.3.2.1.2.{sensor.Index}");
            if (percent == null || alarm == null)
                return null;
            humidity = percent;
            inAlarm |= alarm == TruthTrue;
        }
        return new TrippLiteSensorReading(sensor.Name, sensor.Model, temperature, humidity, inAlarm);
    }

    private static string SensorText(TrippLiteSensorReading reading)
    {
        List<string> parts = [];
        if (reading.TemperatureCelsius is { } temperature)
            parts.Add(FormattableString.Invariant($"{temperature:0.0} °C"));
        if (reading.HumidityPercent is { } humidity)
            parts.Add($"{humidity} %");
        if (reading.InAlarm)
            parts.Add("in alarm");
        return parts.Count == 0 ? "no temperature or humidity probe" : string.Join(", ", parts);
    }

    private void PollAlarms()
    {
        if (_alarmCountUnsupported)
            return;
        var response = _client.Get(AlarmsPresentOid);
        var kind = Kind(response);
        if (kind == ReplyKind.Missed)
        {
            ShowMissed(AlarmCountRead, response);
            return;
        }
        if (kind == ReplyKind.NotSupported)
        {
            _alarmCountUnsupported = true;
            ReadAnswered(AlarmCountRead);
            ShowNotSupported(AlarmCountRead, response);
            return;
        }
        var reply = response.Variables[0].Data.ToString();
        if (!int.TryParse(reply, out var count))
        {
            SetDetail(AlarmsLabel, reply, DetailTone.Warning);
            CountMissedReply(AlarmCountRead, reply);
            return;
        }
        ReadAnswered(AlarmCountRead);

        if (count == 0)
        {
            SetDetail(AlarmsLabel, reply);
            ResolveIssue(AlarmsIssueKey);
            return;
        }
        var alarms = WalkColumn(AlarmDetailColumn).Values.ToList();
        if (alarms.Count == 0)
        {
            SetDetail(AlarmsLabel, $"{reply}, the alarm table returned no rows", DetailTone.Warning);
            RaiseOngoingIssue(AlarmsIssueKey, $"The PDU's alarm count is {count}, but its alarm table returned no rows",
                IssueSeverity.Minor);
            return;
        }
        var unexpected = alarms.Where(alarm => !IsDeliberateState(alarm)).ToList();
        SetDetail(AlarmsLabel, $"{reply}: {string.Join("; ", alarms)}",
            unexpected.Count > 0 ? DetailTone.Warning : DetailTone.Normal);
        if (unexpected.Count == 0)
            ResolveIssue(AlarmsIssueKey);
        else
            RaiseOngoingIssue(AlarmsIssueKey, $"The PDU reports alarms: {string.Join("; ", unexpected)}", IssueSeverity.Minor);
    }

    private bool IsDeliberateState(string alarm) =>
        LoadOffAlarm.IsMatch(alarm)
        || (_inputAWarningDisabled && InputAAlarm.IsMatch(alarm))
        || (_inputBWarningDisabled && InputBAlarm.IsMatch(alarm));

    private TrippLiteInputFeedStatus ShowInputFeed(TrippLiteInputSource source, bool available,
        Dictionary<string, string> voltages, int inputNumber)
    {
        var raw = voltages.FirstOrDefault(pair => pair.Key.StartsWith($"{inputNumber}.")).Value;
        var parsed = int.TryParse(raw, out var tenths);
        var voltageText = raw == null ? "no voltage reading"
            : parsed ? FormattableString.Invariant($"{tenths / 10f:0.0} V")
            : raw;
        var warningDisabled = source == TrippLiteInputSource.A ? _inputAWarningDisabled : _inputBWarningDisabled;
        SetDetail($"Input {source}", $"{(available ? "Available" : "Unavailable")}, {voltageText}",
            available || warningDisabled ? DetailTone.Normal : DetailTone.Warning);
        return new TrippLiteInputFeedStatus(source, available, parsed ? tenths / 10f : 0f);
    }

    private void ReadIdentity()
    {
        if (_unreadIdentity.Contains(FirmwareRead) && Read(FirmwareRead) is { } firmware)
            FirmwareVersion = firmware;
        if (_unreadIdentity.Contains(SerialRead) && Read(SerialRead) is { } serial)
            SerialNumber = serial;
        if (_unreadIdentity.Contains(MacRead))
            Read(MacRead);
    }

    private string? Read(SnmpRead read)
    {
        var response = _client.Get(read.Oid);
        var kind = Kind(response);
        if (kind == ReplyKind.Missed)
        {
            ShowMissed(read, response);
            return null;
        }
        _unreadIdentity.Remove(read);
        ReadAnswered(read);
        if (kind == ReplyKind.NotSupported)
        {
            ShowNotSupported(read, response);
            return string.Empty;
        }
        var value = response.Variables[0].Data.ToString();
        SetDetail(read.Label, value);
        return value;
    }

    private static ReplyKind Kind(SnmpReply response) => response.ErrorStatus switch
    {
        ErrorCode.NoError when response.Variables.Count == 0 => ReplyKind.Missed,
        ErrorCode.NoError when IsUnsupported(response.Variables[0].Data) => ReplyKind.NotSupported,
        ErrorCode.NoError => ReplyKind.Answered,
        ErrorCode.NoSuchName or ErrorCode.NoAccess or ErrorCode.AuthorizationError => ReplyKind.NotSupported,
        _ => ReplyKind.Missed
    };

    private void ShowMissed(SnmpRead read, SnmpReply response)
    {
        if (response.ErrorStatus == ErrorCode.NoError)
        {
            SetDetail(read.Label, NoAnswer, DetailTone.Warning);
            CountMissedReply(read, NoReply);
            return;
        }
        SetDetail(read.Label, response.ErrorText, DetailTone.Warning);
        CountMissedReply(read, response.ErrorText);
    }

    private void CountMissedReply(SnmpRead read, string result)
    {
        var missed = _missedReplies.GetValueOrDefault(read) + 1;
        _missedReplies[read] = missed;
        if (missed < MaxMissedReplies)
            LogInformation("The {Read} query {Oid} got {Result}, asking again on the next poll ({Missed} of {MaxMissedReplies})",
                read.Name, read.Oid, result, missed, MaxMissedReplies);
        else
            RaiseOngoingIssue(read.IssueKey,
                $"The PDU did not give a real reply to the {read.Name} query {read.Oid} {MaxMissedReplies} times in a row, the last was {result}",
                IssueSeverity.Minor);
    }

    private void ReadAnswered(SnmpRead read)
    {
        _missedReplies.Remove(read);
        ResolveIssue(read.IssueKey);
    }

    private void ShowNotSupported(SnmpRead read, SnmpReply response)
    {
        var reply = response.ErrorStatus == ErrorCode.NoError ? response.Variables[0].Data.ToString() : response.ErrorText;
        LogInformation("The PDU does not support the {Label} query {Oid}: it answered {Reply}", read.Label, read.Oid, reply);
        SetDetail(read.Label, $"Not supported: {reply}", DetailTone.Normal);
    }

    private static string Failure(SnmpReply response, string unanswered) =>
        response.ErrorStatus == ErrorCode.NoError ? unanswered : $"was answered with {response.ErrorText}";

    private static bool IsUnsupported(ISnmpData reply) =>
        reply.TypeCode is SnmpType.NoSuchObject or SnmpType.NoSuchInstance or SnmpType.EndOfMibView;

    private int? GetNumber(string oid)
    {
        var response = _client.Get(oid);
        if (response.Variables.Count > 0 && int.TryParse(response.Variables[0].Data.ToString(), out var value))
            return value;
        _lastQueryFailure = Failure(response, NotAnswered);
        return null;
    }

    private string? GetText(string oid)
    {
        var response = _client.Get(oid);
        return response.Variables.Count == 0 || IsUnsupported(response.Variables[0].Data)
            ? null
            : response.Variables[0].Data.ToString();
    }

    private Dictionary<string, string> WalkColumn(string columnOid)
    {
        Dictionary<string, string> results = [];
        var prefix = columnOid.TrimStart('.') + ".";
        var response = _client.Walk(columnOid);
        foreach (var variable in response.Variables)
        {
            var id = variable.Id.ToString();
            if (id.StartsWith(prefix))
                results[id[prefix.Length..]] = variable.Data.ToString();
        }
        if (results.Count == 0)
            _lastQueryFailure = Failure(response, NotAnswered);
        return results;
    }
}
