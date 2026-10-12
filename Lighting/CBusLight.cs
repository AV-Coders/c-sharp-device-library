using AVCoders.Core;

namespace AVCoders.Lighting;

public record CBusLightPreset(string Name, byte Preset) : LightPreset(Name);

public class CBusLight : Light
{
    private const double PercentScale = 100.0;
    private const int MinimumPercent = 0;
    private const int MaximumPercent = 100;
    private const double LevelScale = 255.0;
    private const byte InstantRamp = (byte)CBusRampTime.Instant;
    private const byte TerminateRampCommand = 0x09;
    private const int RampUpDirection = 1;
    private const int RampDownDirection = -1;

    private static readonly Dictionary<CBusRampTime, TimeSpan> RampDurations = new()
    {
        { CBusRampTime.Instant, TimeSpan.Zero },
        { CBusRampTime.FourSeconds, TimeSpan.FromSeconds(4) },
        { CBusRampTime.EightSeconds, TimeSpan.FromSeconds(8) },
        { CBusRampTime.TwelveSeconds, TimeSpan.FromSeconds(12) },
        { CBusRampTime.TwentySeconds, TimeSpan.FromSeconds(20) },
        { CBusRampTime.ThirtySeconds, TimeSpan.FromSeconds(30) },
        { CBusRampTime.FortySeconds, TimeSpan.FromSeconds(40) },
        { CBusRampTime.OneMinute, TimeSpan.FromSeconds(60) },
        { CBusRampTime.OneMinute30, TimeSpan.FromSeconds(90) },
        { CBusRampTime.TwoMinutes, TimeSpan.FromMinutes(2) },
        { CBusRampTime.ThreeMinutes, TimeSpan.FromMinutes(3) },
        { CBusRampTime.FiveMinutes, TimeSpan.FromMinutes(5) },
        { CBusRampTime.SevenMinutes, TimeSpan.FromMinutes(7) },
        { CBusRampTime.TenMinutes, TimeSpan.FromMinutes(10) },
        { CBusRampTime.FifteenMinutes, TimeSpan.FromMinutes(15) },
        { CBusRampTime.SeventeenMinutes, TimeSpan.FromMinutes(17) },
    };

    private readonly byte _application;
    private readonly byte _group;
    private readonly byte _unit;
    private readonly byte _channel;
    private readonly CBusRampTime _defaultRampTime;
    private readonly CBusSerialInterface _interface;
    private readonly TimeProvider _timeProvider;
    private readonly List<CBusLightPreset> _presets = new();
    private byte _level;
    private byte _rampStartLevel;
    private int _rampDirection;
    private DateTimeOffset _rampStartTime;

    public IntHandler? LevelChangeHandlers;

    public CBusLight(string name, CBusSerialInterface @interface, byte group, CBusRampTime defaultRampTime,
        byte unit = 0, byte channel = 0, byte application = CBusSerialInterface.LightingApplication,
        TimeProvider? timeProvider = null) : base(name)
    {
        _application = application;
        _group = group;
        _unit = unit;
        _channel = channel;
        _defaultRampTime = defaultRampTime;
        _interface = @interface;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _interface.GroupMessageHandlers += HandleGroupMessage;
        _interface.ChannelLevelHandlers += HandleChannelLevel;
    }

    public byte Level
    {
        get => _level;
        private set
        {
            Brightness = (uint)Math.Round(value * PercentScale / LevelScale, MidpointRounding.AwayFromZero);
            PowerState = value > CBusSerialInterface.ZeroLevel ? PowerState.On : PowerState.Off;
            if (_level == value)
                return;
            _level = value;
            LevelChangeHandlers?.Invoke(value);
        }
    }

    public override List<LightPreset> Presets() => [.._presets];

    protected override void DoRecallPreset(int preset)
    {
        _interface.SendPointToMultipointPayload(CBusSerialInterface.SceneApplication, [_group, (byte)preset]);
    }

    protected override void DoPowerOn()
    {
        Level = CBusSerialInterface.FullLevel;
        _interface.SendPointToMultipointPayload(_application, [CBusSerialInterface.OnCommand, _group]);
    }

    protected override void DoPowerOff()
    {
        Level = CBusSerialInterface.ZeroLevel;
        _interface.SendPointToMultipointPayload(_application, [CBusSerialInterface.OffCommand, _group]);
    }

    protected override void DoSetLevel(int level)
    {
        SetLevel(level, _defaultRampTime);
    }

    public void SetLevel(int level, CBusRampTime rampTime)
    {
        if (level is < MinimumPercent or > MaximumPercent)
            return;
        byte levelValue = (byte)Math.Round(level * LevelScale / PercentScale, MidpointRounding.AwayFromZero);
        Level = levelValue;
        _interface.SendPointToMultipointPayload(_application, [(byte)rampTime, _group, levelValue]);
    }

    public void RampUp()
    {
        _rampStartLevel = _level;
        _rampStartTime = _timeProvider.GetUtcNow();
        _rampDirection = RampUpDirection;
        _interface.SendPointToMultipointPayload(_application, [(byte)_defaultRampTime, _group, CBusSerialInterface.FullLevel]);
    }

    public void RampDown()
    {
        _rampStartLevel = _level;
        _rampStartTime = _timeProvider.GetUtcNow();
        _rampDirection = RampDownDirection;
        _interface.SendPointToMultipointPayload(_application, [(byte)_defaultRampTime, _group, CBusSerialInterface.ZeroLevel]);
    }

    public void StopRamping()
    {
        _interface.SendPointToMultipointPayload(_application, [TerminateRampCommand, _group]);
    }

    public void StopRampingAtTrackedLevel()
    {
        byte trackedLevel = TrackedRampLevel();
        Level = trackedLevel;
        _interface.SendPointToMultipointPayload(_application, [InstantRamp, _group, trackedLevel]);
    }

    public void RequestStatus()
    {
        _interface.RequestChannelStatus(_unit, _channel);
    }

    private byte TrackedRampLevel()
    {
        TimeSpan rampDuration = RampDurations[_defaultRampTime];
        double progress = rampDuration == TimeSpan.Zero ? 1 : (_timeProvider.GetUtcNow() - _rampStartTime) / rampDuration;
        double level = _rampStartLevel + _rampDirection * LevelScale * progress;
        return (byte)Math.Clamp(Math.Round(level), 0.0, LevelScale);
    }

    private void HandleGroupMessage(CBusGroupMessage message)
    {
        if (message.Application != _application || message.Group != _group)
            return;
        Level = message.Level;
    }

    private void HandleChannelLevel(CBusChannelLevel channelLevel)
    {
        if (channelLevel.Unit != _unit || channelLevel.Channel != _channel)
            return;
        Level = channelLevel.Level;
    }
}
