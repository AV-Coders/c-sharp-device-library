using AVCoders.Core;

namespace AVCoders.Dsp;

public delegate void StringValueHandler(string value);

public abstract class Dsp : DeviceBase
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(1);

    protected readonly ThreadWorker PollWorker;
    private protected readonly TimeProvider TimeProvider;

    protected Dsp(string name, CommunicationClient client, int pollIntervalInMilliseconds)
        : this(name, client, pollIntervalInMilliseconds, null)
    {
    }

    internal Dsp(string name, CommunicationClient client, int pollIntervalInMilliseconds, TimeProvider? timeProvider) : base(name, client)
    {
        TimeProvider = timeProvider ?? TimeProvider.System;
        PollWorker = new ThreadWorker(Poll, TimeSpan.FromMilliseconds(pollIntervalInMilliseconds), true, TimeProvider);
        Task.Run(async () =>
        {
            await Task.Delay(StartupDelay, TimeProvider);
            PollWorker.Restart();
        });
    }

    protected abstract Task Poll(CancellationToken token);
    public abstract void AddControl(VolumeLevelHandler volumeLevelHandler, string controlName);
    public abstract void AddControl(MuteStateHandler muteStateHandler, string muteName);
    public abstract void AddControl(StringValueHandler stringValueHandler, string controlName);

    public abstract void SetLevel(string controlName, int percentage);

    public abstract void LevelUp(string controlName, int amount = 1);
    public abstract void LevelDown(string controlName, int amount = 1);
    public abstract void SetAudioMute(string controlName, MuteState muteState);
    public abstract void ToggleAudioMute(string controlName);
    public abstract void SetValue(string controlName, string value);

    public abstract int GetLevel(string controlName);
    public abstract MuteState GetAudioMute(string controlName);
    public abstract string GetValue(string controlName);

    public abstract Task Reinitialise(CancellationToken token = default);
}

public abstract class AudioBlock
{
}

public enum FaderCurve
{
    Linear,
    Perceptual
}

public class Fader : AudioBlock
{
    private const double PerceptualRangeDb = 60;

    private int _volume = 0; // A percentage, 0 to 100
    private double? _lastDb;
    public double MinGain = -100;
    public double MaxGain = 0;
    public double Step = 0;
    public readonly FaderCurve Curve;

    public int Volume
    {
        get => _volume;
        set
        {
            if (_volume == value)
                return;
            _volume = value;
            Report();
        }
    }

    public VolumeLevelHandler? VolumeLevelHandlers;

    public Fader(VolumeLevelHandler volumeLevelHandler, FaderCurve curve = FaderCurve.Linear)
    {
        VolumeLevelHandlers += volumeLevelHandler;
        Curve = curve;
        CalculateStep();
    }

    private double BottomOfTravel => Curve == FaderCurve.Perceptual
        ? Math.Max(MinGain, MaxGain - PerceptualRangeDb)
        : MinGain;

    public double PercentageToDb(int percentage)
    {
        if (percentage >= 100)
            return MaxGain;
        if (percentage <= 0)
            return MinGain;

        return BottomOfTravel + (Step * percentage);
    }

    public void SetVolumeFromDb(double db)
    {
        _lastDb = db;
        _volume = DbToPercentage(db);
        Report();
    }

    private int DbToPercentage(double db)
    {
        if (db >= MaxGain)
            return 100;
        if (db <= MinGain)
            return 0;

        if (Curve == FaderCurve.Linear)
            return (int)Math.Round(((db - MinGain) * 100) / CalculateRange(MinGain, MaxGain), MidpointRounding.AwayFromZero);

        var bottom = BottomOfTravel;
        return Math.Clamp((int)Math.Round((db - bottom) * 100 / CalculateRange(bottom, MaxGain), MidpointRounding.AwayFromZero), 1, 100);
    }

    public void SetVolumeFromPercentage(double percentage)
    {
        _lastDb = null;
        _volume = (int)percentage;
        Report();
    }

    public double CalculateRange(double min, double max)
    {
        return max - min;
    }

    private void CalculateStep()
    {
        Step = CalculateRange(BottomOfTravel, MaxGain) / 100;
    }

    private void RangeChanged()
    {
        CalculateStep();
        if (Curve == FaderCurve.Perceptual && _lastDb.HasValue)
            Volume = DbToPercentage(_lastDb.Value);
    }

    public void SetMinGain(double gain)
    {
        MinGain = gain;
        RangeChanged();
    }

    public void SetMaxGain(double gain)
    {
        MaxGain = gain;
        RangeChanged();
    }

    public void Report() => VolumeLevelHandlers?.Invoke(Volume);
}

public class Mute : AudioBlock
{
    private MuteState _muteState = MuteState.Unknown;

    public MuteState MuteState
    {
        get => _muteState;
        set
        {
            if (_muteState == value)
                return;
            _muteState = value;
            Report();
        }
    }

    public MuteStateHandler? MuteStateHandlers;

    public Mute(MuteStateHandler muteStateHandler)
    {
        MuteStateHandlers += muteStateHandler;
    }

    public void Report() => MuteStateHandlers?.Invoke(MuteState);
}

public class StringValue : AudioBlock
{
    private string _value = string.Empty;

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value)
                return;
            _value = value;
            Report();
        }
    }

    public StringValueHandler? StringValueHandlers;

    public StringValue(StringValueHandler stringValueHandler)
    {
        StringValueHandlers += stringValueHandler;
    }

    public void Report() => StringValueHandlers?.Invoke(Value);
}