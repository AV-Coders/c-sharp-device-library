using AVCoders.Core;

namespace AVCoders.Display;

public delegate void InputHandler(Input input);

public abstract class Display : VolumeControl, IDevice
{
    protected Input Input = Input.Unknown;
    protected Input DesiredInput = Input.Unknown;
    protected PowerState PowerState = PowerState.Unknown;
    protected PowerState DesiredPowerState = PowerState.Unknown;
    protected CommunicationState CommunicationState = CommunicationState.Unknown;
    protected List<Input> SupportedInputs;
    public LogHandler? LogHandlers;
    public CommunicationStateHandler? CommunicationStateHandlers;
    public PowerStateHandler? PowerStateHandlers;
    public InputHandler? InputHandlers;
    protected int Volume = 0;
    protected MuteState AudioMute = MuteState.Unknown;
    protected MuteState DesiredAudioMute = MuteState.Unknown;
    protected MuteState VideoMute = MuteState.Unknown;
    protected MuteState DesiredVideoMute = MuteState.Unknown;

    protected readonly ThreadWorker PollWorker;
    public PowerState GetCurrentPowerState() => PowerState;

    public CommunicationState GetCurrentCommunicationState() => CommunicationState;

    protected Display(List<Input> supportedInputs, string name, int pollTime = 23) : base(name, VolumeType.Speaker)
    {
        SupportedInputs = supportedInputs;
        PollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(pollTime));
        new Thread(_ =>
        {
            Thread.Sleep(1000);
            PollWorker.Restart();
        }).Start();
    }

    protected abstract Task Poll(CancellationToken token);
    
    protected void Log(string message)
    {
        LogHandlers?.Invoke($"{GetType()} - {message}");
    }

    protected void Error(string message)
    {
        LogHandlers?.Invoke($"{GetType()} - {message}", EventLevel.Error);
    }

    protected void UpdateCommunicationState(CommunicationState state)
    {
        CommunicationState = state;
        CommunicationStateHandlers?.Invoke(state);
    }

    protected void ProcessPowerResponse()
    {
        PowerStateHandlers?.Invoke(PowerState);
        if (PowerState == DesiredPowerState)
            return;
        if (DesiredPowerState == PowerState.Unknown)
            return;
        Log("Forcing Power");
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
        Log("Forcing Input");
        SetInput(DesiredInput);
    }

    public List<Input> GetSupportedInputs() => SupportedInputs;

    public Input GetCurrentInput() => Input;
    
    public int GetCurrentVolume() => Volume;

    public MuteState GetAudioMute() => AudioMute;
    public MuteState GetVideoMute() => VideoMute;

    public void PowerOn()
    {
        DoPowerOn();
        Log("Turning On");
        PowerState = PowerState.On;
        DesiredPowerState = PowerState.On;
        PowerStateHandlers?.Invoke(DesiredPowerState);
    }

    protected abstract void DoPowerOn();

    public void PowerOff()
    {
        DoPowerOff();
        Log("Turning Off");
        PowerState = PowerState.Off;
        DesiredPowerState = PowerState.Off;
        PowerStateHandlers?.Invoke(DesiredPowerState);
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
        Log($"Setting input to {input.ToString()}");
        DesiredInput = input;
        Input = input;
        InputHandlers?.Invoke(Input);
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
        VolumeLevelHandlers?.Invoke(Volume);
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
        MuteStateHandlers?.Invoke(AudioMute);
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