using System.Runtime.CompilerServices;
using AVCoders.Core;

namespace AVCoders.Climate;

public enum TemperzoneUc8FanSpeed
{
    Off = 0,
    Low = 100,
    Medium = 550,
    High = 1000,
}

public class TemperzoneUc8 : DeviceBase, IDisposable, IAsyncDisposable
{
    public const byte DefaultDeviceId = 44;

    public static readonly SerialSpec DefaultSerialSpec = new SerialSpec(SerialBaud.Rate19200, SerialParity.Even,
        SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs485);

    public FloatHandler? OutdoorCoilTemperatureHandler;
    public FloatHandler? IndoorCoilTemperatureHandler;
    public FloatHandler? OutdoorAmbientTemperatureHandler;
    public FloatHandler? SuctionLineTemperatureHandler;
    public FloatHandler? DischargeLineTemperatureHandler;
    public FloatHandler? DeIceSensorTemperatureHandler;
    public FloatHandler? EvaporatingTemperatureHandler;
    public FloatHandler? CondensingTemperatureHandler;
    public FloatHandler? ControllerTemperatureHandler;
    public FloatHandler? SuctionSideSuperheatHandler;
    public FloatHandler? DischargeSideSuperheatHandler;
    public FloatHandler? SupplyAirTemperatureHandler;
    public FloatHandler? ReturnAirTemperatureHandler;
    public FloatHandler? SetpointTemperatureHandler;
    public FloatHandler? RoomTemperatureHandler;
    public FloatHandler? CoolingTargetHandler;
    public FloatHandler? HeatingTargetHandler;
    public IntHandler? SuctionPressureHandler;
    public IntHandler? DischargePressureHandler;
    public IntHandler? IndoorFanSpeedHandler;
    public IntHandler? OutdoorFanSpeedHandler;
    public IntHandler? CapacityHandler;
    public IntHandler? FaultNumberHandler;
    public IntHandler? ControlEnableHandler;
    public IntHandler? FanSpeedRequestHandler;
    public IntHandler? CapacityRequestHandler;
    public IntHandler? UnitModeHandler;
    public IntHandler? MinimumRunTimerHandler;
    public IntHandler? MinimumOffTimerHandler;
    public IntHandler? CompressorStartTimerHandler;
    public IntHandler? CoolingHoldOffTimerHandler;
    public IntHandler? HeatingHoldOffTimerHandler;
    public IntHandler? Exv1PositionHandler;
    public IntHandler? Exv2PositionHandler;
    public BoolHandler? DeIceRequestHandler;
    public BoolHandler? DeIceStatusHandler;
    public BoolHandler? DeIcePermissionHandler;
    public BoolHandler? QuietModeHandler;
    public BoolHandler? DryModeHandler;
    public BoolHandler? EconomyModeHandler;
    public BoolHandler? CompressorRelayHandler;
    public BoolHandler? ReverseValveHandler;
    public BoolHandler? DredHoldOffHandler;
    public BoolHandler? OilRecoveryHandler;
    public HvacModeHandler? ModeHandlers;

    private const ushort OutdoorCoilTemperature = 1;
    private const ushort IndoorCoilTemperature = 2;
    private const ushort OutdoorAmbientTemperature = 3;
    private const ushort SuctionLineTemperature = 4;
    private const ushort DischargeLineTemperature = 5;
    private const ushort DeIceSensorTemperature = 6;
    private const ushort EvaporatingTemperature = 7;
    private const ushort CondensingTemperature = 8;
    private const ushort ControllerTemperature = 9;
    private const ushort SuctionSideSuperheat = 10;
    private const ushort DischargeSideSuperheat = 11;
    private const ushort SuctionLinePressure = 13;
    private const ushort DischargeLinePressure = 14;

    private const ushort ExpansionValve1Position = 26;

    private const ushort ControlEnable = 101;
    private const ushort CompressorPower = 102;
    private const ushort HeatingOrCooling = 103;
    private const ushort RemoteOnOff = 104;
    private const ushort IndoorFanMode = 105;
    private const ushort IndoorFanSpeedControl = 108;
    private const ushort CapacityControl = 109;
    private const ushort OutdoorCoilDeIce = 110;
    private const ushort QuietMode = 111;
    private const ushort DryMode = 112;
    private const ushort Economy = 115;
    private const ushort CoolingSupplyAirTempTarget = 118;
    private const ushort HeatingSupplyAirTempTarget = 119;

    private const ushort SafetyTimerBlock = 201;

    private const ushort OutdoorFanSpeedFb = 401;
    private const ushort IndoorFanSpeedFb = 402;
    private const ushort CapacityFb = 405;
    private const ushort DigitalOutputsFb = 406;
    private const ushort UnitModeFb = 407;

    private const ushort SetpointTemperature = 511;
    private const ushort RoomTemperature = 512;

    private const ushort ControllerIdRegister = 601;

    private const ushort FaultBank1 = 901;
    private const ushort FaultBank2 = 902;
    private const ushort FaultBank3 = 903;
    private const ushort FaultNumber = 905;

    private const ushort UnitAddressRegister = 1001;
    private const ushort CounterBlock2 = 1025;

    private const ushort IndoorUnitTemperatureBlock = 1201;
    private const ushort SupplyAirTemperature = 1205;
    private const ushort ReturnAirTemperature = 1206;

    private const ushort LockoutReset = 1901;

    private const ushort EnableCompressor = 1 << 0;
    private const ushort EnableHeatCool = 1 << 1;
    private const ushort EnableRemoteOnOff = 1 << 2;
    private const ushort EnableFanMode = 1 << 3;
    private const ushort EnableFanSpeed = 1 << 6;
    private const ushort EnableCapacity = 1 << 7;
    private const ushort EnableDeIce = 1 << 8;
    private const ushort EnableQuietMode = 1 << 9;
    private const ushort EnableDryMode = 1 << 10;
    private const ushort EnableEconomyMode = 1 << 13;
    private const ushort KnownEnableBits = 0x27FF;

    private const ushort DeIcePermissionBit = 1 << 0;
    private const ushort ForceDeIceBit = 1 << 4;
    private const ushort CmcRelayBit = 1 << 0;
    private const ushort ReverseValveBit = 1 << 1;
    private const ushort DredHoldOffBit = 1 << 5;
    private const ushort DeIceRequestBit = 1 << 11;
    private const ushort DeIceStatusBit = 1 << 12;
    private const ushort OilRecoveryBit = 1 << 14;

    private const ushort DefaultFanMode = 0b01111;

    private const short SensorUnavailable = -10000;
    private const short NoPressureTransducer = -200;

    private const ushort UnitModeOff = 1;
    private const ushort UnitModeLockout = 12;

    private const int CounterPollInterval = 100;

    private const string LockoutIssueKey = "lockout";
    private const string IdentityIssueKey = "identity";
    private const string WatchdogIssueKey = "watchdog-risk";
    private const string PowerHoldIssueKey = "power-enforcement-hold";
    private const string AdoptedControlIssueKey = "adopted-control";

    private static readonly TimeSpan PowerEnforcementBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(10);

    private static readonly Dictionary<int, ushort> EnableBitCoils = new()
    {
        { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 4 }, { 4, 5 }, { 5, 6 },
        { 6, 7 }, { 7, 8 }, { 8, 9 }, { 9, 10 }, { 10, 11 }, { 13, 12 },
    };

    private static readonly Dictionary<int, string> EnableBitNames = new()
    {
        { 0, "Compressor" }, { 1, "Heating/Cooling" }, { 2, "Remote On/Off" }, { 3, "Fan Mode" },
        { 4, "EXV Mode" }, { 5, "DRED" }, { 6, "Fan Speed" }, { 7, "Capacity" },
        { 8, "De-Ice" }, { 9, "Quiet Mode" }, { 10, "Dry Mode" }, { 13, "Economy Mode" },
    };

    private static readonly string[] SafetyTimerNames =
        ["minimum run", "minimum off", "compressor start", "cooling hold-off", "heating hold-off"];

    private static readonly Dictionary<ushort, string> CounterPoints = new()
    {
        { 1003, "Total Hours Cooling" }, { 1004, "Total Minutes Cooling" },
        { 1005, "Total Hours Heating" }, { 1006, "Total Minutes Heating" },
        { 1007, "Total Hours De-Ice" }, { 1008, "Total Minutes De-Ice" },
        { 1009, "Cooling Cycles" }, { 1010, "Heating Cycles" }, { 1011, "De-Ice Cycles" },
        { 1012, "HP Trip Events" }, { 1013, "LP Trip Events" }, { 1014, "Frost Protection Events" },
        { 1015, "Freeze Protection Events" }, { 1016, "High Temperature Protection Events" },
        { 1017, "High Suction Line Protection Events" }, { 1018, "Overload Protection Events" },
        { 1019, "Low Discharge Superheat Events" }, { 1020, "High Discharge Superheat Events" },
        { 1021, "Power-On Reset Events" }, { 1025, "Indoor Coil Sensor Faults" },
        { 1026, "Outdoor Coil Sensor Faults" }, { 1027, "Outdoor Ambient Sensor Faults" },
        { 1028, "Discharge Line Sensor Faults" }, { 1029, "Suction Line Sensor Faults" },
        { 1030, "De-Ice Sensor Faults" }, { 1031, "High Pressure Transducer Faults" },
        { 1032, "Low Pressure Transducer Faults" }, { 1033, "High Board Temperature Faults" },
        { 1034, "Reverse Cycle Valve Faults" }, { 1035, "IUC Communications Faults" },
        { 1036, "IUC Faults" }, { 1037, "Inverter Faults" }, { 1038, "Compressor Out-Of-Envelope Faults" },
    };

    private record FaultDefinition(int Bit, string Key, string Message, IssueSeverity Severity = IssueSeverity.Major);

    private static readonly FaultDefinition[] FaultBank1Definitions =
    [
        new(0, "fault-hp", "High pressure protection has tripped", IssueSeverity.Critical),
        new(1, "fault-lp", "Low pressure protection has tripped", IssueSeverity.Critical),
        new(2, "fault-overload", "Overload protection has tripped", IssueSeverity.Critical),
        new(3, "fault-frost-protection", "Frost protection has tripped", IssueSeverity.Critical),
        new(4, "fault-freeze-protection", "Freeze protection has tripped", IssueSeverity.Critical),
        new(5, "fault-high-temperature-protection", "High temperature protection has tripped", IssueSeverity.Critical),
        new(6, "fault-high-suction-line-protection", "High suction line temperature or pressure protection has tripped", IssueSeverity.Critical),
        new(7, "fault-flood-protection", "Flood protection has tripped", IssueSeverity.Critical),
        new(8, "fault-water-flow-protection", "Water flow protection has tripped", IssueSeverity.Critical),
        new(9, "fault-low-discharge-superheat", "Low discharge superheat protection has tripped", IssueSeverity.Critical),
        new(10, "fault-outdoor-fan-comms", "No communications with the outdoor fan speed controller"),
        new(11, "fault-indoor-fan-comms", "No communications with the indoor fan speed controller"),
        new(12, "fault-low-pressure-transducer", "Low pressure transducer fault"),
        new(13, "fault-high-pressure-transducer", "High pressure transducer fault"),
        new(14, "fault-suction-line-sensor", "Suction line temperature sensor fault"),
        new(15, "fault-discharge-line-sensor", "Discharge line temperature sensor fault"),
    ];

    private static readonly FaultDefinition[] FaultBank2Definitions =
    [
        new(0, "fault-de-ice-sensor", "De-ice temperature sensor fault"),
        new(1, "fault-outdoor-coil-sensor", "Outdoor coil temperature sensor fault"),
        new(2, "fault-indoor-coil-sensor", "Indoor coil temperature sensor fault"),
        new(3, "fault-outdoor-ambient-sensor", "Outdoor ambient temperature sensor fault"),
        new(4, "fault-superheat-unknown", "Superheat is unknown"),
        new(5, "fault-thermostat-comms", "No communications with the thermostat"),
        new(6, "fault-master-comms", "No communications with the UC8 master board"),
        new(7, "fault-slave-1-comms", "No communications with the UC8 slave 1 board"),
        new(8, "fault-slave-2-comms", "No communications with the UC8 slave 2 board"),
        new(9, "fault-slave-3-comms", "No communications with the UC8 slave 3 board"),
        new(10, "fault-dip-switch-read", "Problem with reading the DIP switches"),
        new(11, "fault-illegal-fan-selection", "Illegal combination of indoor and outdoor fan selection"),
        new(12, "fault-de-ice-sensor-required", "The unit requires an outdoor coil de-ice temperature sensor"),
        new(13, "fault-board-temperature", "The UC8 controller board temperature is too high"),
        new(14, "fault-supply-voltage", "UC8 controller supply voltage fault"),
        new(15, "fault-slave-fault", "A slave system reports a fault"),
    ];

    private static readonly FaultDefinition[] FaultBank3Definitions =
    [
        new(0, "fault-analogue-input", "0-10V analogue input fault"),
        new(1, "fault-high-discharge-superheat", "High discharge superheat protection has tripped", IssueSeverity.Critical),
        new(2, "fault-pressure-transducer-readings", "Problem with readings from the pressure transducers"),
        new(3, "fault-reverse-cycle-valve", "Reverse cycle valve fault"),
        new(4, "fault-tzt100-dip-switches", "Invalid DIP switch settings on the TZT-100 thermostat"),
        new(5, "fault-iuc-comms", "No communications with the indoor unit controller"),
        new(6, "fault-iuc-fault", "The indoor unit controller reports a fault"),
        new(7, "fault-compressor-driver", "The variable speed compressor driver reports a fault", IssueSeverity.Critical),
        new(8, "fault-compression-ratio-high", "Compression ratio too high", IssueSeverity.Critical),
        new(9, "fault-compression-ratio-low", "Compression ratio too low", IssueSeverity.Critical),
        new(10, "fault-evaporating-temperature-high", "Evaporating temperature too high", IssueSeverity.Critical),
        new(11, "fault-condensing-temperature-low", "Condensing temperature too low", IssueSeverity.Critical),
    ];

    private static readonly string[] TemperatureDeadbandPoints =
    [
        TemperzoneUc8History.OutdoorCoilTemperaturePoint, TemperzoneUc8History.IndoorCoilTemperaturePoint,
        TemperzoneUc8History.OutdoorAmbientTemperaturePoint, TemperzoneUc8History.SuctionLineTemperaturePoint,
        TemperzoneUc8History.DischargeLineTemperaturePoint, TemperzoneUc8History.DeIceSensorTemperaturePoint,
        TemperzoneUc8History.EvaporatingTemperaturePoint, TemperzoneUc8History.CondensingTemperaturePoint,
        TemperzoneUc8History.ControllerTemperaturePoint, TemperzoneUc8History.SuctionSideSuperheatPoint,
        TemperzoneUc8History.DischargeSideSuperheatPoint, TemperzoneUc8History.SupplyAirTemperaturePoint,
        TemperzoneUc8History.ReturnAirTemperaturePoint, TemperzoneUc8History.SetpointTemperaturePoint,
        TemperzoneUc8History.RoomTemperaturePoint,
    ];

    private readonly ModbusClient _client;
    private readonly byte _deviceId;
    private readonly ThreadWorker _pollWorker;
    private readonly object _controlLock = new();
    private readonly Dictionary<ushort, ushort> _lastCommanded = new();
    private readonly int[] _safetyTimers = new int[5];
    private ushort _desiredEnableMask;
    private ushort _lastCompressorMask;
    private ushort _deIceControl;
    private ushort _forceDeIcePriorPermission;
    private int _desiredFanSpeed;
    private int _armingGeneration;
    private int _sequencesInFlight;
    private bool _reapplyInFlight;
    private volatile bool _identityVerified;
    private volatile bool _identityPending = true;
    private volatile bool _safetyTimersRead;
    private bool _forceDeIceActive;
    private DateTimeOffset _forceDeIceStarted;
    private DateTimeOffset _lastPowerEnforcement = DateTimeOffset.MinValue;
    private int _pollCycle;
    private HvacMode _mode = HvacMode.Unknown;

    public TemperzoneUc8(string name, ModbusClient client, byte deviceId = DefaultDeviceId,
        string? historyDirectory = null) : base(name, client)
    {
        _client = client;
        _deviceId = deviceId;
        History = new TemperzoneUc8History(name, historyDirectory);
        foreach (var point in TemperatureDeadbandPoints)
            History.SetDeadband(point, 0.2f);
        History.SetDeadband(TemperzoneUc8History.SuctionLinePressurePoint, 5f);
        History.SetDeadband(TemperzoneUc8History.DischargeLinePressurePoint, 5f);
        client.ConnectionStateHandlers += HandleConnectionState;
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(10));
        _pollWorker.Restart();
    }

    public TemperzoneUc8History History { get; }

    public bool IdentityVerified => _identityVerified;

    public ushort ExpectedIdCode { get; set; } = 210;

    public int ControlEnableReadback { get; private set; }

    public bool DeIcePermission { get; private set; }

    public TimeSpan ForceDeIceTimeout { get; set; } = TimeSpan.FromMinutes(15);

    public HvacMode Mode
    {
        get => _mode;
        private set
        {
            if (_mode == value)
                return;
            _mode = value;
            ModeHandlers?.Invoke(value);
        }
    }

    public IReadOnlyList<string> GetArmedFunctions() =>
        EnableBitNames.Where(kv => (ControlEnableReadback & (1 << kv.Key)) != 0)
            .OrderBy(kv => kv.Key)
            .Select(kv => kv.Value)
            .ToList();

    public async ValueTask DisposeAsync()
    {
        CommunicationClient.ConnectionStateHandlers -= HandleConnectionState;
        await _pollWorker.Stop();
        ushort mask;
        lock (_controlLock)
            mask = (ushort)(_desiredEnableMask | (ControlEnableReadback & KnownEnableBits));
        if (mask != 0)
            await DoReleaseControl();
        History.Dispose();
        LogBaseRegistry.Deregister(this);
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        if (!DisposeAsync().AsTask().Wait(DisposeTimeout))
            using (PushProperties(nameof(Dispose)))
                LogWarning("Dispose timed out waiting for the driver to release control");
    }

    private void HandleConnectionState(ConnectionState state)
    {
        if (state != ConnectionState.Connected)
            _identityPending = true;
    }

    private async Task Poll(CancellationToken token)
    {
        if (CommunicationClient.ConnectionState != ConnectionState.Connected)
        {
            using (PushProperties("Poll"))
                LogDebug("Not polling");
            UpdateWatchdogRisk(false);
            return;
        }

        var successes = 0;
        var failures = 0;

        async Task RunBlock(string blockName, ushort start, ushort count, Action<ushort[]> process)
        {
            if (await TryPollBlock(blockName, start, count, process, token))
                successes++;
            else
                failures++;
        }

        if (_identityPending)
        {
            try
            {
                var info = await _client.ReadHoldingRegisters(_deviceId, ControllerIdRegister, 2, token);
                var address = await _client.ReadHoldingRegisters(_deviceId, UnitAddressRegister, 1, token);
                ProcessIdentity(info, address);
                ResolveIssue("unanswered-identity");
                successes++;
            }
            catch (Exception e) when (e is ModbusException or TimeoutException)
            {
                failures++;
                RaiseMomentaryIssue($"The identity poll was not answered: {e.Message}", key: "unanswered-identity",
                    escalateAfter: 3);
            }
        }

        await RunBlock("faults", FaultBank1, 5, ProcessFaults);
        await RunBlock("temperatures", OutdoorCoilTemperature, 14, ProcessTemperatures);
        await RunBlock("expansion valves", ExpansionValve1Position, 2, ProcessExpansionValves);
        await RunBlock("control", ControlEnable, 21, ProcessControlBlock);
        await RunBlock("safety timers", SafetyTimerBlock, 16, ProcessSafetyTimers);
        await RunBlock("outputs", OutdoorFanSpeedFb, 7, ProcessOutputs);
        await RunBlock("thermostat", SetpointTemperature, 2, ProcessThermostatBlock);
        await RunBlock("indoor temperatures", IndoorUnitTemperatureBlock, 6, ProcessIndoorTemperatures);

        if (_pollCycle % CounterPollInterval == 0)
        {
            await RunBlock("unit history", UnitAddressRegister, 24, values => ProcessCounters(values, UnitAddressRegister));
            await RunBlock("unit history continued", CounterBlock2, 14, values => ProcessCounters(values, CounterBlock2));
        }

        _pollCycle++;

        if (successes == 0 && failures > 0)
            CommunicationState = CommunicationState.Error;
        else if (successes > 0)
            CommunicationState = CommunicationState.Okay;
        UpdateWatchdogRisk(successes > 0);
    }

    private async Task<bool> TryPollBlock(string blockName, ushort start, ushort count, Action<ushort[]> process,
        CancellationToken token)
    {
        try
        {
            process(await _client.ReadHoldingRegisters(_deviceId, start, count, token));
            ResolveIssue($"unanswered-{blockName}");
            return true;
        }
        catch (Exception e) when (e is ModbusException or TimeoutException)
        {
            RaiseMomentaryIssue($"The {blockName} poll was not answered: {e.Message}",
                key: $"unanswered-{blockName}", escalateAfter: 3);
            return false;
        }
    }

    private void UpdateWatchdogRisk(bool pollHealthy)
    {
        ushort mask;
        lock (_controlLock)
            mask = _desiredEnableMask;
        if (mask != 0 && !pollHealthy)
            RaiseOngoingIssue(WatchdogIssueKey,
                "BMS control is armed and the UC8 is not answering polls. The unit will trip F22 and may stop within 5 minutes",
                IssueSeverity.Critical);
        else
            ResolveIssue(WatchdogIssueKey);
    }

    private void ProcessIdentity(ushort[] info, ushort[] address)
    {
        if (info.Length < 2 || address.Length < 1)
            return;
        if (info[0] == ExpectedIdCode && address[0] == _deviceId)
        {
            _identityVerified = true;
            _identityPending = false;
            ResolveIssue(IdentityIssueKey);
            using (PushProperties(nameof(ProcessIdentity)))
                LogInformation("UC8 identity verified, software version {Version}", info[1]);
            return;
        }
        _identityVerified = false;
        RaiseOngoingIssue(IdentityIssueKey,
            $"Identity check failed (ID code {info[0]}, reported address {address[0]}, expected {ExpectedIdCode} at {_deviceId}). The register addressing is likely offset or the device is not a UC8. Control writes are disabled",
            IssueSeverity.Critical);
    }

    public void OverrideIdentityCheck()
    {
        _identityVerified = true;
        _identityPending = false;
        ResolveIssue(IdentityIssueKey);
        using (PushProperties(nameof(OverrideIdentityCheck)))
            LogWarning("The UC8 identity check has been manually overridden");
        AddEvent(EventType.DriverState, "The UC8 identity check was manually overridden");
    }

    private void ProcessTemperatures(ushort[] values)
    {
        InvokeTemperature(values, OutdoorCoilTemperature, OutdoorCoilTemperatureHandler, TemperzoneUc8History.OutdoorCoilTemperaturePoint);
        InvokeTemperature(values, IndoorCoilTemperature, IndoorCoilTemperatureHandler, TemperzoneUc8History.IndoorCoilTemperaturePoint);
        InvokeTemperature(values, OutdoorAmbientTemperature, OutdoorAmbientTemperatureHandler, TemperzoneUc8History.OutdoorAmbientTemperaturePoint);
        InvokeTemperature(values, SuctionLineTemperature, SuctionLineTemperatureHandler, TemperzoneUc8History.SuctionLineTemperaturePoint);
        InvokeTemperature(values, DischargeLineTemperature, DischargeLineTemperatureHandler, TemperzoneUc8History.DischargeLineTemperaturePoint);
        InvokeTemperature(values, DeIceSensorTemperature, DeIceSensorTemperatureHandler, TemperzoneUc8History.DeIceSensorTemperaturePoint);
        InvokeTemperature(values, EvaporatingTemperature, EvaporatingTemperatureHandler, TemperzoneUc8History.EvaporatingTemperaturePoint);
        InvokeTemperature(values, CondensingTemperature, CondensingTemperatureHandler, TemperzoneUc8History.CondensingTemperaturePoint);
        InvokeTemperature(values, ControllerTemperature, ControllerTemperatureHandler, TemperzoneUc8History.ControllerTemperaturePoint);
        InvokeTemperature(values, SuctionSideSuperheat, SuctionSideSuperheatHandler, TemperzoneUc8History.SuctionSideSuperheatPoint);
        InvokeTemperature(values, DischargeSideSuperheat, DischargeSideSuperheatHandler, TemperzoneUc8History.DischargeSideSuperheatPoint);
        InvokePressure(values, SuctionLinePressure, SuctionPressureHandler, TemperzoneUc8History.SuctionLinePressurePoint);
        InvokePressure(values, DischargeLinePressure, DischargePressureHandler, TemperzoneUc8History.DischargeLinePressurePoint);
    }

    private void InvokeTemperature(ushort[] values, ushort register, FloatHandler? handler, string point)
    {
        var index = register - OutdoorCoilTemperature;
        if (values.Length <= index)
            return;
        var raw = (short)values[index];
        if (raw == SensorUnavailable)
            return;
        var value = raw / 100f;
        History.Record(point, value);
        handler?.Invoke(value);
    }

    private void InvokePressure(ushort[] values, ushort register, IntHandler? handler, string point)
    {
        var index = register - OutdoorCoilTemperature;
        if (values.Length <= index)
            return;
        var raw = (short)values[index];
        if (raw == NoPressureTransducer)
            return;
        History.Record(point, raw);
        handler?.Invoke(raw);
    }

    private void ProcessControlBlock(ushort[] values)
    {
        if (values.Length == 0)
            return;
        ControlEnableReadback = values[0];
        ControlEnableHandler?.Invoke(values[0]);
        ProcessControlEnable(values[0]);
        if (values.Length <= HeatingSupplyAirTempTarget - ControlEnable)
            return;
        var fanSpeedRequest = values[IndoorFanSpeedControl - ControlEnable];
        History.Record(TemperzoneUc8History.FanSpeedRequestPoint, fanSpeedRequest);
        FanSpeedRequestHandler?.Invoke(fanSpeedRequest);
        var capacityRequest = values[CapacityControl - ControlEnable];
        History.Record(TemperzoneUc8History.CapacityRequestPoint, capacityRequest);
        CapacityRequestHandler?.Invoke(capacityRequest);
        DeIcePermission = (values[OutdoorCoilDeIce - ControlEnable] & DeIcePermissionBit) != 0;
        DeIcePermissionHandler?.Invoke(DeIcePermission);
        var quiet = values[QuietMode - ControlEnable] != 0;
        History.Record(TemperzoneUc8History.QuietModePoint, quiet ? 1 : 0);
        QuietModeHandler?.Invoke(quiet);
        var dry = values[DryMode - ControlEnable] != 0;
        History.Record(TemperzoneUc8History.DryModePoint, dry ? 1 : 0);
        DryModeHandler?.Invoke(dry);
        var economy = values[Economy - ControlEnable] != 0;
        History.Record(TemperzoneUc8History.EconomyModePoint, economy ? 1 : 0);
        EconomyModeHandler?.Invoke(economy);
        var coolingTarget = (short)values[CoolingSupplyAirTempTarget - ControlEnable] / 100f;
        History.Record(TemperzoneUc8History.CoolingSupplyAirTargetPoint, coolingTarget);
        CoolingTargetHandler?.Invoke(coolingTarget);
        var heatingTarget = (short)values[HeatingSupplyAirTempTarget - ControlEnable] / 100f;
        History.Record(TemperzoneUc8History.HeatingSupplyAirTargetPoint, heatingTarget);
        HeatingTargetHandler?.Invoke(heatingTarget);
    }

    private void ProcessControlEnable(ushort readback)
    {
        ushort mask;
        bool inFlight;
        lock (_controlLock)
        {
            mask = _desiredEnableMask;
            inFlight = _sequencesInFlight > 0 || _reapplyInFlight;
        }
        var orphaned = (ushort)(readback & ~mask & KnownEnableBits);
        if (orphaned != 0 && !inFlight)
        {
            lock (_controlLock)
            {
                _desiredEnableMask = (ushort)(_desiredEnableMask | orphaned);
                mask = _desiredEnableMask;
            }
            var names = EnableBitNames.Where(kv => (orphaned & (1 << kv.Key)) != 0)
                .OrderBy(kv => kv.Key)
                .Select(kv => kv.Value);
            RaiseOngoingIssue(AdoptedControlIssueKey,
                $"Adopted pre-existing BMS control of: {string.Join(", ", names)}");
        }
        if (mask == 0)
            return;
        if ((readback & mask) == mask)
            return;
        if (inFlight)
            return;
        using (PushProperties(nameof(ProcessControlEnable)))
            LogWarning("Control enable readback is {Readback}, re-arming {Desired} and re-applying commands", readback, mask);
        _ = ReapplyControl();
    }

    private async Task ReapplyControl()
    {
        lock (_controlLock)
        {
            if (_reapplyInFlight)
                return;
            _reapplyInFlight = true;
            _armingGeneration++;
        }
        try
        {
            int generation;
            ushort mask;
            List<KeyValuePair<ushort, ushort>> commanded;
            lock (_controlLock)
            {
                generation = _armingGeneration;
                mask = _desiredEnableMask;
                commanded = _lastCommanded.OrderBy(kv => kv.Key).ToList();
            }
            foreach (var bit in ArmedBits(mask))
            {
                if (SequenceStale(generation))
                    return;
                await _client.WriteCoil(_deviceId, EnableBitCoils[bit], true);
            }
            foreach (var pair in commanded)
            {
                if (SequenceStale(generation))
                    return;
                await _client.WriteRegister(_deviceId, pair.Key, pair.Value);
            }
            CommunicationState = CommunicationState.Okay;
        }
        catch (Exception e)
        {
            HandleWriteFailure(e);
        }
        finally
        {
            lock (_controlLock)
                _reapplyInFlight = false;
        }
    }

    private void ProcessOutputs(ushort[] values)
    {
        if (values.Length < 7)
            return;
        var outdoorFanSpeed = values[OutdoorFanSpeedFb - OutdoorFanSpeedFb];
        History.Record(TemperzoneUc8History.OutdoorFanSpeedPoint, outdoorFanSpeed);
        OutdoorFanSpeedHandler?.Invoke(outdoorFanSpeed);
        var indoorFanSpeed = values[IndoorFanSpeedFb - OutdoorFanSpeedFb];
        History.Record(TemperzoneUc8History.IndoorFanSpeedPoint, indoorFanSpeed);
        IndoorFanSpeedHandler?.Invoke(indoorFanSpeed);
        var capacity = values[CapacityFb - OutdoorFanSpeedFb] / 10f;
        History.Record(TemperzoneUc8History.CapacityPoint, capacity);
        CapacityHandler?.Invoke((int)capacity);
        var outputs = values[DigitalOutputsFb - OutdoorFanSpeedFb];
        var compressorRelay = (outputs & CmcRelayBit) != 0;
        History.Record(TemperzoneUc8History.CompressorRelayPoint, compressorRelay ? 1 : 0);
        CompressorRelayHandler?.Invoke(compressorRelay);
        var reverseValve = (outputs & ReverseValveBit) != 0;
        History.Record(TemperzoneUc8History.ReverseValvePoint, reverseValve ? 1 : 0);
        ReverseValveHandler?.Invoke(reverseValve);
        var dredHoldOff = (outputs & DredHoldOffBit) != 0;
        History.Record(TemperzoneUc8History.DredHoldOffPoint, dredHoldOff ? 1 : 0);
        DredHoldOffHandler?.Invoke(dredHoldOff);
        var oilRecovery = (outputs & OilRecoveryBit) != 0;
        History.Record(TemperzoneUc8History.OilRecoveryPoint, oilRecovery ? 1 : 0);
        OilRecoveryHandler?.Invoke(oilRecovery);
        var deIceRequest = (outputs & DeIceRequestBit) != 0;
        var deIceStatus = (outputs & DeIceStatusBit) != 0;
        History.Record(TemperzoneUc8History.DeIceRequestPoint, deIceRequest ? 1 : 0);
        History.Record(TemperzoneUc8History.DeIceStatusPoint, deIceStatus ? 1 : 0);
        DeIceRequestHandler?.Invoke(deIceRequest);
        DeIceStatusHandler?.Invoke(deIceStatus);
        var clearForce = false;
        ushort restore = 0;
        lock (_controlLock)
        {
            if (_forceDeIceActive &&
                (deIceStatus || DateTimeOffset.UtcNow - _forceDeIceStarted >= ForceDeIceTimeout))
            {
                _forceDeIceActive = false;
                _deIceControl = _forceDeIcePriorPermission;
                restore = _deIceControl;
                clearForce = true;
            }
        }
        if (clearForce)
            _ = WriteControl(EnableDeIce, OutdoorCoilDeIce, restore);
        ProcessUnitMode(values[UnitModeFb - OutdoorFanSpeedFb]);
    }

    private void ProcessUnitMode(ushort mode)
    {
        UnitModeHandler?.Invoke(mode);
        if (mode == UnitModeLockout)
            RaiseOngoingIssue(LockoutIssueKey, "The unit is locked out. Investigate the cause, then reset the lockout", IssueSeverity.Critical);
        else
            ResolveIssue(LockoutIssueKey);

        PowerState = mode switch
        {
            UnitModeOff or UnitModeLockout => PowerState.Off,
            _ => PowerState.On
        };

        Mode = mode switch
        {
            >= 2 and <= 4 => HvacMode.Cool,
            >= 5 and <= 7 => HvacMode.Heat,
            _ => Mode
        };

        History.Record(TemperzoneUc8History.UnitModePoint, mode);
        History.Record(TemperzoneUc8History.PowerStatePoint, PowerState switch
        {
            PowerState.On => TemperzoneUc8History.PowerOnCode,
            PowerState.Off => TemperzoneUc8History.PowerOffCode,
            _ => TemperzoneUc8History.PowerUnknownCode
        });
        History.Record(TemperzoneUc8History.HvacModePoint, Mode switch
        {
            HvacMode.Heat => TemperzoneUc8History.HvacHeatCode,
            HvacMode.Cool => TemperzoneUc8History.HvacCoolCode,
            HvacMode.Dry => TemperzoneUc8History.HvacDryCode,
            HvacMode.FanOnly => TemperzoneUc8History.HvacFanOnlyCode,
            _ => TemperzoneUc8History.HvacUnknownCode
        });

        if (mode != UnitModeLockout)
            EnforcePowerState();
    }

    private void EnforcePowerState()
    {
        if (!_safetyTimersRead)
            return;
        if (DesiredPowerState == PowerState.Unknown || PowerState == DesiredPowerState)
        {
            ResolveIssue(PowerHoldIssueKey);
            ProcessPowerState();
            return;
        }
        var activeTimer = Array.FindIndex(_safetyTimers, t => t > 0);
        if (activeTimer >= 0)
        {
            RaiseOngoingIssue(PowerHoldIssueKey,
                $"Power enforcement is waiting for the {SafetyTimerNames[activeTimer]} safety timer ({_safetyTimers[activeTimer]}s remaining)",
                IssueSeverity.Minor);
            return;
        }
        ResolveIssue(PowerHoldIssueKey);
        if (DateTimeOffset.UtcNow - _lastPowerEnforcement < PowerEnforcementBackoff)
            return;
        _lastPowerEnforcement = DateTimeOffset.UtcNow;
        ProcessPowerState();
    }

    private void ProcessSafetyTimers(ushort[] values)
    {
        if (values.Length < 16)
            return;
        UpdateSafetyTimer(0, values[0], TemperzoneUc8History.MinimumRunTimerPoint, MinimumRunTimerHandler);
        UpdateSafetyTimer(1, values[1], TemperzoneUc8History.MinimumOffTimerPoint, MinimumOffTimerHandler);
        UpdateSafetyTimer(2, values[2], TemperzoneUc8History.CompressorStartTimerPoint, CompressorStartTimerHandler);
        UpdateSafetyTimer(3, values[14], TemperzoneUc8History.CoolingHoldOffTimerPoint, CoolingHoldOffTimerHandler);
        UpdateSafetyTimer(4, values[15], TemperzoneUc8History.HeatingHoldOffTimerPoint, HeatingHoldOffTimerHandler);
        _safetyTimersRead = true;
    }

    private void UpdateSafetyTimer(int index, ushort value, string point, IntHandler? handler)
    {
        _safetyTimers[index] = value;
        History.Record(point, value);
        handler?.Invoke(value);
    }

    private void ProcessExpansionValves(ushort[] values)
    {
        if (values.Length < 2)
            return;
        History.Record(TemperzoneUc8History.Exv1PositionPoint, values[0]);
        Exv1PositionHandler?.Invoke(values[0]);
        History.Record(TemperzoneUc8History.Exv2PositionPoint, values[1]);
        Exv2PositionHandler?.Invoke(values[1]);
    }

    private void ProcessFaults(ushort[] values)
    {
        if (values.Length < 5)
            return;
        History.Record(TemperzoneUc8History.FaultBank1Point, values[FaultBank1 - FaultBank1]);
        History.Record(TemperzoneUc8History.FaultBank2Point, values[FaultBank2 - FaultBank1]);
        History.Record(TemperzoneUc8History.FaultBank3Point, values[FaultBank3 - FaultBank1]);
        History.Record(TemperzoneUc8History.FaultNumberPoint, values[FaultNumber - FaultBank1]);
        ProcessFaultBank(values[FaultBank1 - FaultBank1], FaultBank1Definitions);
        ProcessFaultBank(values[FaultBank2 - FaultBank1], FaultBank2Definitions);
        ProcessFaultBank(values[FaultBank3 - FaultBank1], FaultBank3Definitions);
        FaultNumberHandler?.Invoke(values[FaultNumber - FaultBank1]);
    }

    private void ProcessFaultBank(ushort value, FaultDefinition[] definitions)
    {
        foreach (var definition in definitions)
        {
            if ((value & (1 << definition.Bit)) != 0)
                RaiseOngoingIssue(definition.Key, definition.Message, definition.Severity);
            else
                ResolveIssue(definition.Key);
        }
    }

    private void ProcessCounters(ushort[] values, ushort blockStart)
    {
        foreach (var pair in CounterPoints)
        {
            var index = pair.Key - blockStart;
            if (index < 0 || index >= values.Length)
                continue;
            History.Record(pair.Value, values[index]);
        }
    }

    private void ProcessThermostatBlock(ushort[] values)
    {
        InvokeThermostatTemperature(values, SetpointTemperature, SetpointTemperatureHandler,
            TemperzoneUc8History.SetpointTemperaturePoint);
        InvokeThermostatTemperature(values, RoomTemperature, RoomTemperatureHandler,
            TemperzoneUc8History.RoomTemperaturePoint);
    }

    private void InvokeThermostatTemperature(ushort[] values, ushort register, FloatHandler? handler, string point)
    {
        var index = register - SetpointTemperature;
        if (values.Length <= index)
            return;
        var raw = (short)values[index];
        if (raw == SensorUnavailable)
            return;
        var value = raw / 100f;
        History.Record(point, value);
        handler?.Invoke(value);
    }

    private void ProcessIndoorTemperatures(ushort[] values)
    {
        InvokeIndoorTemperature(values, SupplyAirTemperature, SupplyAirTemperatureHandler,
            TemperzoneUc8History.SupplyAirTemperaturePoint);
        InvokeIndoorTemperature(values, ReturnAirTemperature, ReturnAirTemperatureHandler,
            TemperzoneUc8History.ReturnAirTemperaturePoint);
    }

    private void InvokeIndoorTemperature(ushort[] values, ushort register, FloatHandler? handler, string point)
    {
        var index = register - IndoorUnitTemperatureBlock;
        if (values.Length <= index)
            return;
        var raw = (short)values[index];
        if (raw == SensorUnavailable)
            return;
        var value = raw / 100f;
        History.Record(point, value);
        handler?.Invoke(value);
    }

    private bool ControlAllowed([CallerMemberName] string? method = null)
    {
        if (_identityVerified)
            return true;
        using (PushProperties(method))
            LogError("Control write refused, the UC8 identity has not been verified");
        return false;
    }

    public override void PowerOn()
    {
        if (!ControlAllowed())
            return;
        DesiredPowerState = PowerState.On;
        _ = WritePower(true);
    }

    public override void PowerOff()
    {
        if (!ControlAllowed())
            return;
        DesiredPowerState = PowerState.Off;
        _ = WritePower(false);
    }

    private async Task WritePower(bool on)
    {
        BeginSequence();
        try
        {
            var generation = await ArmAndGetGeneration(EnableCompressor | EnableRemoteOnOff);
            if (generation == null || SequenceStale(generation.Value))
                return;
            if (on)
            {
                await WriteCommand(RemoteOnOff, 1);
                if (SequenceStale(generation.Value))
                    return;
                ushort compressorMask;
                lock (_controlLock)
                {
                    _lastCompressorMask = (ushort)(_lastCompressorMask | 1);
                    compressorMask = _lastCompressorMask;
                }
                await WriteCommand(CompressorPower, compressorMask);
            }
            else
            {
                lock (_controlLock)
                    _lastCompressorMask = 0;
                await WriteCommand(CompressorPower, 0);
                if (SequenceStale(generation.Value))
                    return;
                await WriteCommand(RemoteOnOff, 0);
            }
            CommunicationState = CommunicationState.Okay;
        }
        catch (Exception e)
        {
            HandleWriteFailure(e);
        }
        finally
        {
            EndSequence();
        }
    }

    public void SetMode(HvacMode mode)
    {
        if (!ControlAllowed())
            return;
        switch (mode)
        {
            case HvacMode.Cool:
                _ = WriteHeatCool(0);
                break;
            case HvacMode.Heat:
                _ = WriteHeatCool(1);
                break;
            case HvacMode.Dry:
                _ = WriteControl(EnableDryMode, DryMode, 1);
                break;
            case HvacMode.FanOnly:
                _ = WriteFanOnly();
                break;
        }
    }

    public void SetCompressor(bool on)
    {
        if (!ControlAllowed())
            return;
        ushort mask;
        lock (_controlLock)
        {
            mask = on ? (ushort)(_lastCompressorMask | 1) : (ushort)(_lastCompressorMask & ~1);
            _lastCompressorMask = mask;
        }
        _ = WriteControl(EnableCompressor, CompressorPower, mask);
    }

    public void SetCompressors(bool master, bool slave1 = false, bool slave2 = false, bool slave3 = false)
    {
        if (!ControlAllowed())
            return;
        var mask = (ushort)((master ? 1 : 0) | (slave1 ? 2 : 0) | (slave2 ? 4 : 0) | (slave3 ? 8 : 0));
        lock (_controlLock)
            _lastCompressorMask = mask;
        _ = WriteControl(EnableCompressor, CompressorPower, mask);
    }

    public void SetFanSpeed(TemperzoneUc8FanSpeed speed) => SetFanSpeed((int)speed);

    public void SetFanSpeed(int speed)
    {
        if (!ControlAllowed())
            return;
        var clamped = Math.Clamp(speed, 0, 1000);
        _desiredFanSpeed = clamped;
        _ = WriteFanSpeed((ushort)clamped);
    }

    public void SetFanMode(bool fixedSpeed, bool fanOnInDeadband, bool fanOnDuringDeIce, bool fanOnDuringHeatingStart,
        bool runOnAfterCooling)
    {
        if (!ControlAllowed())
            return;
        var value = (ushort)((fixedSpeed ? 1 : 0) | (fanOnInDeadband ? 2 : 0) | (fanOnDuringDeIce ? 4 : 0) |
                             (fanOnDuringHeatingStart ? 8 : 0) | (runOnAfterCooling ? 16 : 0));
        _ = WriteControl(EnableFanMode, IndoorFanMode, value);
    }

    public void SetCapacity(int percent)
    {
        if (!ControlAllowed())
            return;
        _ = WriteControl(EnableCapacity, CapacityControl, (ushort)Math.Clamp(percent, 0, 100));
    }

    public void SetQuietMode(bool on)
    {
        if (!ControlAllowed())
            return;
        _ = WriteControl(EnableQuietMode, QuietMode, (ushort)(on ? 1 : 0));
    }

    public void SetDryMode(bool on)
    {
        if (!ControlAllowed())
            return;
        _ = WriteControl(EnableDryMode, DryMode, (ushort)(on ? 1 : 0));
    }

    public void SetEconomyMode(bool on)
    {
        if (!ControlAllowed())
            return;
        _ = WriteControl(EnableEconomyMode, Economy, (ushort)(on ? 1 : 0));
    }

    public void SetDeIcePermission(bool allowed)
    {
        if (!ControlAllowed())
            return;
        ushort value;
        lock (_controlLock)
        {
            _deIceControl = allowed ? DeIcePermissionBit : (ushort)0;
            if (_forceDeIceActive)
            {
                _forceDeIcePriorPermission = _deIceControl;
                value = (ushort)(_deIceControl | ForceDeIceBit);
            }
            else
                value = _deIceControl;
        }
        _ = WriteControl(EnableDeIce, OutdoorCoilDeIce, value);
    }

    public void ForceDeIce()
    {
        if (!ControlAllowed())
            return;
        lock (_controlLock)
        {
            _forceDeIcePriorPermission = _deIceControl;
            _deIceControl = DeIcePermissionBit;
            _forceDeIceActive = true;
            _forceDeIceStarted = DateTimeOffset.UtcNow;
        }
        _ = WriteControl(EnableDeIce, OutdoorCoilDeIce, DeIcePermissionBit | ForceDeIceBit);
    }

    public void SetSupplyAirTargets(float coolingCelsius, float heatingCelsius)
    {
        if (!ControlAllowed())
            return;
        var cooling = (ushort)Math.Clamp((int)(coolingCelsius * 100), 400, 2000);
        var heating = (ushort)Math.Clamp((int)(heatingCelsius * 100), 2000, 4500);
        _ = WriteSupplyAirTargets(cooling, heating);
    }

    public void ResetLockout() => _ = WriteLockoutReset();

    public void ReleaseControl() => _ = DoReleaseControl();

    private async Task DoReleaseControl()
    {
        ushort mask;
        lock (_controlLock)
        {
            mask = (ushort)(_desiredEnableMask | (ControlEnableReadback & KnownEnableBits));
            _desiredEnableMask = 0;
            _lastCommanded.Clear();
            _armingGeneration++;
            _sequencesInFlight++;
        }
        try
        {
            DesiredPowerState = PowerState.Unknown;
            ResolveIssue(WatchdogIssueKey);
            ResolveIssue(PowerHoldIssueKey);
            ResolveIssue(AdoptedControlIssueKey);
            if (mask == 0)
                return;
            foreach (var bit in ArmedBits(mask))
                await _client.WriteCoil(_deviceId, EnableBitCoils[bit], false);
            CommunicationState = CommunicationState.Okay;
        }
        catch (Exception e)
        {
            HandleWriteFailure(e);
        }
        finally
        {
            EndSequence();
        }
    }

    private async Task WriteControl(ushort enableBits, ushort register, ushort value)
    {
        BeginSequence();
        try
        {
            var generation = await ArmAndGetGeneration(enableBits);
            if (generation == null || SequenceStale(generation.Value))
                return;
            await WriteCommand(register, value);
            CommunicationState = CommunicationState.Okay;
        }
        catch (Exception e)
        {
            HandleWriteFailure(e);
        }
        finally
        {
            EndSequence();
        }
    }

    private async Task WriteHeatCool(ushort value)
    {
        BeginSequence();
        try
        {
            var generation = await ArmAndGetGeneration(EnableHeatCool);
            if (generation == null || SequenceStale(generation.Value))
                return;
            await WriteCommand(HeatingOrCooling, value);
            ushort mask;
            lock (_controlLock)
                mask = _desiredEnableMask;
            if ((mask & EnableDryMode) != 0)
            {
                if (SequenceStale(generation.Value))
                    return;
                await WriteCommand(DryMode, 0);
            }
            if ((mask & EnableCompressor) != 0)
            {
                if (SequenceStale(generation.Value))
                    return;
                ushort compressorMask;
                lock (_controlLock)
                {
                    if (_lastCompressorMask == 0)
                        _lastCompressorMask = 1;
                    compressorMask = _lastCompressorMask;
                }
                await WriteCommand(CompressorPower, compressorMask);
            }
            CommunicationState = CommunicationState.Okay;
        }
        catch (Exception e)
        {
            HandleWriteFailure(e);
        }
        finally
        {
            EndSequence();
        }
    }

    private async Task WriteFanOnly()
    {
        BeginSequence();
        try
        {
            var generation = await ArmAndGetGeneration(EnableCompressor | EnableFanSpeed);
            if (generation == null || SequenceStale(generation.Value))
                return;
            lock (_controlLock)
                _lastCompressorMask = 0;
            await WriteCommand(CompressorPower, 0);
            if (SequenceStale(generation.Value))
                return;
            var speed = _desiredFanSpeed > 0 ? _desiredFanSpeed : (int)TemperzoneUc8FanSpeed.Low;
            await WriteCommand(IndoorFanSpeedControl, (ushort)speed);
            CommunicationState = CommunicationState.Okay;
        }
        catch (Exception e)
        {
            HandleWriteFailure(e);
        }
        finally
        {
            EndSequence();
        }
    }

    private async Task WriteFanSpeed(ushort speed)
    {
        BeginSequence();
        try
        {
            var generation = await ArmAndGetGeneration(EnableFanMode | EnableFanSpeed);
            if (generation == null || SequenceStale(generation.Value))
                return;
            ushort fanMode;
            lock (_controlLock)
                fanMode = _lastCommanded.TryGetValue(IndoorFanMode, out var previous)
                    ? previous
                    : DefaultFanMode;
            await WriteCommand(IndoorFanMode, fanMode);
            if (SequenceStale(generation.Value))
                return;
            await WriteCommand(IndoorFanSpeedControl, speed);
            CommunicationState = CommunicationState.Okay;
        }
        catch (Exception e)
        {
            HandleWriteFailure(e);
        }
        finally
        {
            EndSequence();
        }
    }

    private async Task WriteSupplyAirTargets(ushort cooling, ushort heating)
    {
        BeginSequence();
        try
        {
            int generation;
            lock (_controlLock)
                generation = _armingGeneration;
            await WriteCommand(CoolingSupplyAirTempTarget, cooling);
            if (SequenceStale(generation))
                return;
            await WriteCommand(HeatingSupplyAirTempTarget, heating);
            CommunicationState = CommunicationState.Okay;
        }
        catch (Exception e)
        {
            HandleWriteFailure(e);
        }
        finally
        {
            EndSequence();
        }
    }

    private async Task WriteLockoutReset()
    {
        try
        {
            await _client.WriteRegister(_deviceId, LockoutReset, 21930);
            await _client.WriteRegister(_deviceId, LockoutReset, 3855);
            CommunicationState = CommunicationState.Okay;
        }
        catch (Exception e)
        {
            HandleWriteFailure(e);
        }
    }

    private async Task WriteCommand(ushort register, ushort value)
    {
        await _client.WriteRegister(_deviceId, register, value);
        lock (_controlLock)
            _lastCommanded[register] = value;
    }

    private async Task<int?> ArmAndGetGeneration(ushort enableBits)
    {
        ushort newBits;
        int generation;
        lock (_controlLock)
        {
            newBits = (ushort)(enableBits & ~_desiredEnableMask);
            _desiredEnableMask = (ushort)(_desiredEnableMask | enableBits);
            if (newBits != 0)
                _armingGeneration++;
            generation = _armingGeneration;
        }
        var first = true;
        foreach (var bit in ArmedBits(newBits))
        {
            if (!first && SequenceStale(generation))
                return null;
            first = false;
            await _client.WriteCoil(_deviceId, EnableBitCoils[bit], true);
        }
        return generation;
    }

    private bool SequenceStale(int generation)
    {
        lock (_controlLock)
            return generation != _armingGeneration;
    }

    private void BeginSequence()
    {
        lock (_controlLock)
            _sequencesInFlight++;
    }

    private void EndSequence()
    {
        lock (_controlLock)
            _sequencesInFlight--;
    }

    private static IEnumerable<int> ArmedBits(ushort mask) =>
        EnableBitCoils.Keys.OrderBy(b => b).Where(bit => (mask & (1 << bit)) != 0);

    private void HandleWriteFailure(Exception e)
    {
        using (PushProperties(nameof(HandleWriteFailure)))
            LogException(e);
        CommunicationState = CommunicationState.Error;
    }
}
