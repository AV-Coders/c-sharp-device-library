﻿using AVCoders.Core;

namespace AVCoders.Dsp;

public delegate void StringValueHandler(string value);

public abstract class Dsp : IDevice
{
    protected PowerState PowerState = PowerState.Unknown;
    protected CommunicationState CommunicationState = CommunicationState.Unknown;
    public LogHandler? LogHandlers;
    public CommunicationStateHandler? CommunicationStateHandlers;

    
    public PowerState GetCurrentPowerState() => PowerState;

    public CommunicationState GetCurrentCommunicationState() => CommunicationState;
    
    protected void Log(string message) => LogHandlers?.Invoke(message);
    protected void Error(string message) => LogHandlers?.Invoke(message, EventLevel.Error);

    protected void UpdateCommunicationState(CommunicationState state)
    {
        CommunicationState = state;
        CommunicationStateHandlers?.Invoke(state);
    }

    public abstract void PowerOn();

    public abstract void PowerOff();

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
}

public abstract class AudioBlock { }

public abstract class Fader : AudioBlock
    {
        public double MinGain = -100;
        public double MaxGain = 0;
        public double Step = 0;
        public int Volume = 0; // A percentage, 0 to 100
        public VolumeLevelHandler? VolumeLevelHandlers;
        private readonly bool _convertLogarithmicToLinear;

        protected Fader(VolumeLevelHandler volumeLevelHandler, bool convertLogarithmicToLinear)
        {
            VolumeLevelHandlers += volumeLevelHandler;
            _convertLogarithmicToLinear = convertLogarithmicToLinear;
            CalculateStep();
        }

        public double PercentageToDb(int percentage)
        {
            if (percentage >=  100)
                return MaxGain;
            if (percentage <= 0)
                return MinGain;

            return MinGain + (Step * percentage);
        }

        public void SetVolumeFromDb(double db)
        {
            Volume = (int) (((db - MinGain) * 100) / CalculateRange(MinGain, MaxGain));
        }

        public void SetVolumeFromPercentage(double precentage)
        {
            Volume = (int)precentage;
        }

        public double CalculateRange(double min, double max)
        {
            return max-min;
        }

        private void CalculateStep()
        {
            Step = _convertLogarithmicToLinear?
                (Math.Log(CalculateRange(MinGain, MaxGain)))/(100 - 1)
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

    public abstract class Mute : AudioBlock
    {
        public MuteState MuteState { get; set; } = MuteState.Unknown;
        public MuteStateHandler? MuteStateHandlers;

        public Mute(MuteStateHandler muteStateHandler)
        {
            MuteStateHandlers += muteStateHandler;
        }

        public void Report() => MuteStateHandlers?.Invoke(MuteState);

    }

    public abstract class StringValue : AudioBlock
    {
        public string Value { get; set; } = "";
        public StringValueHandler? StringValueHandlers;

        public StringValue(StringValueHandler stringValueHandler)
        {
            StringValueHandlers += stringValueHandler;
            Value = "";
        }

        public void Report() => StringValueHandlers?.Invoke(Value);

    }