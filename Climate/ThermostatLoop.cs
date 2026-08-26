using AVCoders.Core;

namespace AVCoders.Climate;

public enum ThermostatLoopMode
{
    Heat,
    Cool,
    Dry,
    Auto
}

public enum ThermostatLoopState
{
    Disabled,
    Waiting,
    Idle,
    Cooling,
    Heating,
    InputStale
}

public delegate void ThermostatLoopStateHandler(ThermostatLoopState state);

public class ThermostatLoop : LogBase, IDisposable
{
    public const string HeatSetpointPoint = "Loop Heat Setpoint";
    public const string CoolSetpointPoint = "Loop Cool Setpoint";
    public const string ModePoint = "Loop Mode";
    public const string RoomTemperaturePoint = "Loop Room Temperature";
    public const string StatePoint = "Loop State";
    public const string CommandedCapacityPoint = "Loop Commanded Capacity";

    public const int DisabledCode = 0;
    public const int WaitingCode = 1;
    public const int IdleCode = 2;
    public const int CoolingCode = 3;
    public const int HeatingCode = 4;
    public const int InputStaleCode = 5;

    public const int ModeHeatCode = 1;
    public const int ModeCoolCode = 2;
    public const int ModeDryCode = 3;
    public const int ModeAutoCode = 4;

    private const string StaleInputIssueKey = "stale-input";

    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(10);

    public ThermostatLoopStateHandler? StateHandlers;

    public float Deadband { get; set; } = 1.0f;
    public float MinSetpointGap { get; set; } = 1.0f;
    public TimeSpan ChangeoverLockout { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan MinDecisionInterval { get; set; } = TimeSpan.FromMinutes(3);
    public bool ProportionalCapacity { get; set; } = true;
    public float CapacityGainPercentPerDegree { get; set; } = 30f;
    public int MinCapacityPercent { get; set; } = 40;
    public int MaxCapacityPercent { get; set; } = 100;
    public int FixedCapacityPercent { get; set; } = 65;
    public TimeSpan StaleInputTimeout { get; set; } = TimeSpan.FromMinutes(5);

    private readonly TemperzoneUc8 _unit;
    private readonly ThreadWorker _worker;
    private readonly object _loopLock = new();
    private ThermostatLoopState _state = ThermostatLoopState.Disabled;
    private ThermostatLoopMode _mode = ThermostatLoopMode.Auto;
    private float _heatSetpoint = 20f;
    private float _coolSetpoint = 24f;
    private float? _roomTemperature;
    private DateTimeOffset? _roomTemperatureReceivedAt;
    private HvacMode _activeAction = HvacMode.Unknown;
    private HvacMode? _commandedUnitMode;
    private bool? _compressorCommanded;
    private int? _commandedCapacity;
    private DateTimeOffset _lastActionChange = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCompressorDecision = DateTimeOffset.MinValue;

    public ThermostatLoop(string name, TemperzoneUc8 unit) : base(name)
    {
        _unit = unit;
        _worker = new ThreadWorker(Run, TimeSpan.FromSeconds(30));
        _worker.Restart();
    }

    public bool Enabled { get; private set; }

    public ThermostatLoopMode Mode => _mode;

    public float HeatSetpoint => _heatSetpoint;

    public float CoolSetpoint => _coolSetpoint;

    public float? RoomTemperature => _roomTemperature;

    public ThermostatLoopState State
    {
        get => _state;
        private set
        {
            if (_state == value)
                return;
            _state = value;
            AddEvent(EventType.DriverState, $"Thermostat loop state is now {value}");
            _unit.History.Record(StatePoint, StateCode(value));
            StateHandlers?.Invoke(value);
        }
    }

    private static int StateCode(ThermostatLoopState state) => state switch
    {
        ThermostatLoopState.Disabled => DisabledCode,
        ThermostatLoopState.Waiting => WaitingCode,
        ThermostatLoopState.Idle => IdleCode,
        ThermostatLoopState.Cooling => CoolingCode,
        ThermostatLoopState.Heating => HeatingCode,
        _ => InputStaleCode
    };

    private static int ModeCode(ThermostatLoopMode mode) => mode switch
    {
        ThermostatLoopMode.Heat => ModeHeatCode,
        ThermostatLoopMode.Cool => ModeCoolCode,
        ThermostatLoopMode.Dry => ModeDryCode,
        _ => ModeAutoCode
    };

    public void Enable()
    {
        lock (_loopLock)
        {
            Enabled = true;
            EvaluateLocked();
        }
    }

    public void Disable()
    {
        lock (_loopLock)
        {
            if (!Enabled)
                return;
            Enabled = false;
            _unit.SetCompressor(false);
            _compressorCommanded = false;
            State = ThermostatLoopState.Disabled;
        }
    }

    public void SetMode(ThermostatLoopMode mode)
    {
        lock (_loopLock)
        {
            if (_mode != mode)
            {
                _mode = mode;
                AddEvent(EventType.DriverState, $"Thermostat loop mode is now {mode}");
                _unit.History.Record(ModePoint, ModeCode(mode));
            }
            EvaluateLocked();
        }
    }

    public void SetHeatSetpoint(float celsius)
    {
        lock (_loopLock)
        {
            _heatSetpoint = Math.Clamp(celsius, 10f, 35f);
            if (_coolSetpoint < _heatSetpoint + MinSetpointGap)
            {
                _coolSetpoint = Math.Clamp(_heatSetpoint + MinSetpointGap, 10f, 35f);
                if (_coolSetpoint < _heatSetpoint + MinSetpointGap)
                    _heatSetpoint = _coolSetpoint - MinSetpointGap;
                using (PushProperties(nameof(SetHeatSetpoint)))
                    LogInformation("Cool setpoint adjusted to {Setpoint} to maintain the minimum gap", _coolSetpoint);
                AddEvent(EventType.DriverState,
                    $"Cool setpoint adjusted to {_coolSetpoint} to maintain the minimum gap");
                _unit.History.Record(CoolSetpointPoint, _coolSetpoint);
            }
            _unit.History.Record(HeatSetpointPoint, _heatSetpoint);
            EvaluateLocked();
        }
    }

    public void SetCoolSetpoint(float celsius)
    {
        lock (_loopLock)
        {
            _coolSetpoint = Math.Clamp(celsius, 10f, 35f);
            if (_heatSetpoint > _coolSetpoint - MinSetpointGap)
            {
                _heatSetpoint = Math.Clamp(_coolSetpoint - MinSetpointGap, 10f, 35f);
                if (_heatSetpoint > _coolSetpoint - MinSetpointGap)
                    _coolSetpoint = _heatSetpoint + MinSetpointGap;
                using (PushProperties(nameof(SetCoolSetpoint)))
                    LogInformation("Heat setpoint adjusted to {Setpoint} to maintain the minimum gap", _heatSetpoint);
                AddEvent(EventType.DriverState,
                    $"Heat setpoint adjusted to {_heatSetpoint} to maintain the minimum gap");
                _unit.History.Record(HeatSetpointPoint, _heatSetpoint);
            }
            _unit.History.Record(CoolSetpointPoint, _coolSetpoint);
            EvaluateLocked();
        }
    }

    public void SetRoomTemperature(float celsius)
    {
        lock (_loopLock)
        {
            _roomTemperature = celsius;
            _roomTemperatureReceivedAt = DateTimeOffset.UtcNow;
            _unit.History.Record(RoomTemperaturePoint, celsius);
            EvaluateLocked();
        }
    }

    public void Dispose()
    {
        var stop = _worker.Stop();
        lock (_loopLock)
        {
            if (Enabled)
            {
                Enabled = false;
                _unit.SetCompressor(false);
                _compressorCommanded = false;
                State = ThermostatLoopState.Disabled;
            }
        }
        if (!stop.Wait(DisposeTimeout))
            using (PushProperties(nameof(Dispose)))
                LogWarning("The thermostat loop worker did not stop in time");
        LogBaseRegistry.Deregister(this);
        GC.SuppressFinalize(this);
    }

    private Task Run(CancellationToken token)
    {
        Evaluate();
        return Task.CompletedTask;
    }

    private void Evaluate()
    {
        lock (_loopLock)
            EvaluateLocked();
    }

    private void EvaluateLocked()
    {
        if (!Enabled)
        {
            State = ThermostatLoopState.Disabled;
            return;
        }
        if (!_unit.IdentityVerified)
        {
            if (State != ThermostatLoopState.Waiting)
                using (PushProperties(nameof(EvaluateLocked)))
                    LogInformation("Waiting for the UC8 identity check before controlling the unit");
            State = ThermostatLoopState.Waiting;
            return;
        }
        if (_roomTemperature == null || _roomTemperatureReceivedAt == null)
        {
            if (State != ThermostatLoopState.Waiting)
                using (PushProperties(nameof(EvaluateLocked)))
                    LogInformation("Waiting for a room temperature input before controlling the unit");
            State = ThermostatLoopState.Waiting;
            return;
        }
        var now = DateTimeOffset.UtcNow;
        if (now - _roomTemperatureReceivedAt.Value > StaleInputTimeout)
        {
            if (State == ThermostatLoopState.InputStale)
                return;
            _unit.SetCompressor(false);
            _compressorCommanded = false;
            _lastCompressorDecision = now;
            RaiseOngoingIssue(StaleInputIssueKey,
                "The room temperature input is stale. The loop failed safe and the compressor was commanded off");
            State = ThermostatLoopState.InputStale;
            return;
        }
        if (State == ThermostatLoopState.InputStale)
            ResolveIssue(StaleInputIssueKey);

        var room = _roomTemperature.Value;
        var halfBand = Deadband / 2f;
        var action = DetermineAction(room, halfBand, now);
        bool? demand = null;
        if (action == HvacMode.Cool)
        {
            if (room > _coolSetpoint + halfBand)
                demand = true;
            else if (room < _coolSetpoint - halfBand)
                demand = false;
        }
        else if (action == HvacMode.Heat)
        {
            if (room < _heatSetpoint - halfBand)
                demand = true;
            else if (room > _heatSetpoint + halfBand)
                demand = false;
        }

        var unitMode = _mode == ThermostatLoopMode.Dry ? HvacMode.Dry
            : action == HvacMode.Heat ? HvacMode.Heat : HvacMode.Cool;

        var running = _compressorCommanded == true;
        if (demand == true && !running)
        {
            if (now - _lastCompressorDecision >= MinDecisionInterval)
            {
                CommandUnitMode(unitMode);
                _unit.SetCompressor(true);
                _compressorCommanded = true;
                _lastCompressorDecision = now;
                running = true;
            }
        }
        else if (demand == false && running)
        {
            if (now - _lastCompressorDecision >= MinDecisionInterval)
            {
                _unit.SetCompressor(false);
                _compressorCommanded = false;
                _lastCompressorDecision = now;
                running = false;
            }
        }
        else if (running && unitMode != _commandedUnitMode)
            CommandUnitMode(unitMode);

        if (running)
        {
            var error = action == HvacMode.Heat
                ? Math.Abs(room - _heatSetpoint)
                : Math.Abs(room - _coolSetpoint);
            var capacity = ComputeCapacity(error);
            if (capacity != _commandedCapacity)
            {
                _unit.SetCapacity(capacity);
                _commandedCapacity = capacity;
                _unit.History.Record(CommandedCapacityPoint, capacity);
            }
        }

        State = running
            ? action == HvacMode.Heat ? ThermostatLoopState.Heating : ThermostatLoopState.Cooling
            : ThermostatLoopState.Idle;
    }

    private HvacMode DetermineAction(float room, float halfBand, DateTimeOffset now)
    {
        switch (_mode)
        {
            case ThermostatLoopMode.Heat:
                _activeAction = HvacMode.Heat;
                return _activeAction;
            case ThermostatLoopMode.Cool:
            case ThermostatLoopMode.Dry:
                _activeAction = HvacMode.Cool;
                return _activeAction;
        }
        if (_activeAction is not (HvacMode.Cool or HvacMode.Heat))
        {
            if (room > _coolSetpoint + halfBand)
                SetActiveAction(HvacMode.Cool, now);
            else if (room < _heatSetpoint - halfBand)
                SetActiveAction(HvacMode.Heat, now);
        }
        else if (_activeAction == HvacMode.Heat && room > _coolSetpoint + halfBand &&
                 now - _lastActionChange >= ChangeoverLockout)
            SetActiveAction(HvacMode.Cool, now);
        else if (_activeAction == HvacMode.Cool && room < _heatSetpoint - halfBand &&
                 now - _lastActionChange >= ChangeoverLockout)
            SetActiveAction(HvacMode.Heat, now);
        return _activeAction;
    }

    private void SetActiveAction(HvacMode action, DateTimeOffset now)
    {
        if (_activeAction == action)
            return;
        _activeAction = action;
        _lastActionChange = now;
    }

    private void CommandUnitMode(HvacMode mode)
    {
        if (_commandedUnitMode == mode)
            return;
        _unit.SetMode(mode);
        _commandedUnitMode = mode;
    }

    private int ComputeCapacity(float error)
    {
        var raw = ProportionalCapacity ? error * CapacityGainPercentPerDegree : FixedCapacityPercent;
        var clamped = Math.Clamp(raw, MinCapacityPercent, MaxCapacityPercent);
        return (int)(Math.Round(clamped / 5f, MidpointRounding.AwayFromZero) * 5);
    }
}
