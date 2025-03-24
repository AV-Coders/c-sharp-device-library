﻿using AVCoders.Core;
using Serilog;
using Serilog.Context;

namespace AVCoders.Display;

public delegate void InputHandler(Input input);

public abstract class Display : VolumeControl, IDevice
{
    public List<Input> SupportedInputs { get; }
    private Input _input = Input.Unknown;
    protected Input DesiredInput = Input.Unknown;
    private PowerState _powerState = PowerState.Unknown;
    protected PowerState DesiredPowerState = PowerState.Unknown;
    private CommunicationState _communicationState = CommunicationState.Unknown;
    private readonly Input? _defaultInput;
    public CommunicationStateHandler? CommunicationStateHandlers;
    public PowerStateHandler? PowerStateHandlers;
    public InputHandler? InputHandlers;
    private int _volume = 0;
    private MuteState _audioMute = MuteState.Unknown;
    protected MuteState DesiredAudioMute = MuteState.Unknown;
    private MuteState _videoMute = MuteState.Unknown;
    protected MuteState DesiredVideoMute = MuteState.Unknown;

    protected readonly ThreadWorker PollWorker;

    protected Display(List<Input> supportedInputs, string name, Input? defaultInput, int pollTime = 23) : base(name, VolumeType.Speaker)
    {
        SupportedInputs = supportedInputs;
        _defaultInput = defaultInput;
        PollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(pollTime));
        new Thread(_ =>
        {
            Thread.Sleep(1000);
            PollWorker.Restart();
        }).Start();
    }
    
    public Input Input
    {
        get => _input;
        protected set
        {
            if (_input == value) 
                return;
            _input = value;
            InputHandlers?.Invoke(value);
        }
    }

    public PowerState PowerState
    {
        get => _powerState;
        protected set
        {
            if(_powerState == value)
                return;
            _powerState = value;
            PowerStateHandlers?.Invoke(value);
        }
    }
    
    public int Volume
    {
        get => _volume;
        protected set
        {
            if(_volume == value)
                return;
            _volume = value;
            VolumeLevelHandlers?.Invoke(value);
        }
    }


    public MuteState AudioMute
    {
        get => _audioMute;
        protected set
        {
            if (_audioMute == value)
                return;
            _audioMute = value;
            MuteStateHandlers?.Invoke(AudioMute);
        }
    }

    public MuteState VideoMute
    {
        get => _videoMute;
        protected set => _videoMute = value;
    }
    
    public CommunicationState CommunicationState
    {
        get => _communicationState;
        protected set
        {
            if(_communicationState == value)
                return;
            _communicationState = value;
            CommunicationStateHandlers?.Invoke(value);
        }
    }

    protected abstract Task Poll(CancellationToken token);
    
    protected void Debug(string message)
    {
        using (LogContext.PushProperty("class", GetType()))
        using (LogContext.PushProperty("instance_name", Name))
        {
            Log.Debug(message);
        }
    }

    protected void Error(string message)
    {
        using (LogContext.PushProperty("class", GetType()))
        using (LogContext.PushProperty("instance_name", Name))
        {
            Log.Error(message);
        }
    }

    protected void ProcessPowerResponse()
    {
        if (PowerState == DesiredPowerState)
            return;
        if (DesiredPowerState == PowerState.Unknown)
            return;
        Debug("Forcing Power");
        if (DesiredPowerState == PowerState.Off)
            PowerOff();
        else if (DesiredPowerState == PowerState.On) 
            PowerOn();
    }
    
    protected void ProcessInputResponse()
    {
        InputHandlers?.Invoke(Input);
        if (Input == DesiredInput)
            return;
        if (DesiredInput == Input.Unknown)
            return;
        Debug("Forcing Input");
        SetInput(DesiredInput);
    }

    public void TogglePower()
    {
        if(PowerState == PowerState.On)
            PowerOff();
        else
            PowerOn();
    }
    
    public void PowerOn()
    {
        DoPowerOn();
        Debug("Turning On");
        PowerState = PowerState.On;
        DesiredPowerState = PowerState.On;
        if(_defaultInput != null)
            DesiredInput = _defaultInput.Value;
    }

    protected abstract void DoPowerOn();

    public void PowerOff()
    {
        DoPowerOff();
        Debug("Turning Off");
        PowerState = PowerState.Off;
        DesiredPowerState = PowerState.Off;
    }

    protected abstract void DoPowerOff();

    public void SetInput(Input input)
    {
        if (!SupportedInputs.Contains(input))
        {
            Error($"Requested Input {input} is not available");
            return;
        }
        DoSetInput(input);
        Debug($"Setting input to {input.ToString()}");
        DesiredInput = input;
        Input = input;
    }

    protected abstract void DoSetInput(Input input);

    public void SetVolume(int volume)
    {
        if (volume is > 100 or < 0)
        {
            Error($"Volume needs to be a value between 0 and 100, it's {volume}");
            return;
        }
        DoSetVolume(volume);
        Volume = volume;
    }

    protected abstract void DoSetVolume(int percentage);

    public override void LevelUp(int amount)
    {
        int newVolume = Volume + amount;
        if(newVolume > 100)
            newVolume = 100;
        SetVolume(newVolume);
    }

    public override void LevelDown(int amount)
    {
        int newVolume = Volume - amount;
        if(newVolume < 0)
            newVolume = 0;
        SetVolume(newVolume);
    }

    public override void SetLevel(int level) => SetVolume(level);

    public override void SetAudioMute(MuteState state)
    {
        DesiredAudioMute = state;
        DoSetAudioMute(state);
        AudioMute = state;
    }

    protected abstract void DoSetAudioMute(MuteState state);

    public override void ToggleAudioMute()
    {
        switch (AudioMute)
        {
            case MuteState.On:
                SetAudioMute(MuteState.Off);
                break;
            default:
                SetAudioMute(MuteState.On);
                break;
        }
    }
}