﻿using System.ComponentModel;
using AVCoders.Core;

namespace AVCoders.Dsp;

public delegate void StringValueHandler(string value);

public abstract class Dsp : DeviceBase
{

    protected readonly ThreadWorker PollWorker;

    protected Dsp(string name, int pollIntervalInSeconds) : base(name)
    {
        PollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(pollIntervalInSeconds));
        Task.Run(async () =>
        {
            await Task.Delay(1000);
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
    public abstract String GetValue(string controlName);

    public abstract void Reinitialise();
}

public abstract class AudioBlock
{
}

public class Fader : AudioBlock
{
    private int _volume = 0; // A percentage, 0 to 100
    public double MinGain = -100;
    public double MaxGain = 0;
    public double Step = 0;

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
    private readonly bool _convertLogarithmicToLinear;

    public Fader(VolumeLevelHandler volumeLevelHandler, bool convertLogarithmicToLinear)
    {
        VolumeLevelHandlers += volumeLevelHandler;
        _convertLogarithmicToLinear = convertLogarithmicToLinear;
        CalculateStep();
    }

    public double PercentageToDb(int percentage)
    {
        if (percentage >= 100)
            return MaxGain;
        if (percentage <= 0)
            return MinGain;

        return MinGain + (Step * percentage);
    }

    public void SetVolumeFromDb(double db)
    {
        Volume = (int)(((db - MinGain) * 100) / CalculateRange(MinGain, MaxGain));
        Report();
    }

    public void SetVolumeFromPercentage(double percentage)
    {
        Volume = (int)percentage;
        Report();
    }

    public double CalculateRange(double min, double max)
    {
        return max - min;
    }

    private void CalculateStep()
    {
        Step = _convertLogarithmicToLinear
            ? (Math.Log(CalculateRange(MinGain, MaxGain))) / (100 - 1)
            : CalculateRange(MinGain, MaxGain) / 100;
    }

    public void SetMinGain(double gain)
    {
        MinGain = gain;
        CalculateStep();
    }

    public void SetMaxGain(double gain)
    {
        MaxGain = gain;
        CalculateStep();
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
    private string _value = String.Empty;

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