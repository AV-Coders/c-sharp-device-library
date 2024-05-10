using AVCoders.Core;

namespace AVCoders.Display;

public delegate void InputHandler(Input input);

public abstract class Display : IDevice
{
    protected Input Input = Input.Unknown;
    protected Input DesiredInput = Input.Unknown;
    protected PowerState PowerState = PowerState.Unknown;
    protected PowerState DesiredPowerState = PowerState.Unknown;
    protected CommunicationState CommunicationState = CommunicationState.Unknown;
    public LogHandler? LogHandlers;
    public CommunicationStateHandler? CommunicationStateHandlers;
    public PowerStateHandler? PowerStateHandlers;
    public InputHandler? InputHandlers;
    public VolumeLevelHandler? VolumeLevelHandlers;
    public MuteStateHandler? MuteStateHandlers;
    protected int Volume = 0;
    protected MuteState AudioMute = MuteState.Unknown;
    protected MuteState DesiredAudioMute = MuteState.Unknown;
    protected MuteState VideoMute = MuteState.Unknown;
    protected MuteState DesiredVideoMute = MuteState.Unknown;

    protected readonly ThreadWorker PollWorker;
    public PowerState GetCurrentPowerState() => PowerState;

    public CommunicationState GetCurrentCommunicationState() => CommunicationState;

    protected Display()
    {
        PollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(23));
        PollWorker.Restart();
    }

    protected abstract void Poll();
    
    protected void Log(string message)
    {
        LogHandlers?.Invoke($"Display - {message}");
    }

    protected void UpdateCommunicationState(CommunicationState state)
    {
        CommunicationState = state;
        CommunicationStateHandlers?.Invoke(state);
    }

    protected void ProcessPowerResponse()
    {
        PowerStateHandlers?.Invoke(PowerState);
        AlignPowerState();
    }
    
    private void AlignPowerState()
    {
        if (PowerState == DesiredPowerState)
            return;
        if (DesiredPowerState == PowerState.Unknown)
            return;
        Log("Forcing Power");
        if (DesiredPowerState == PowerState.Off)
            PowerOff();
        else if (DesiredPowerState != PowerState.On)
            PowerOn();
    }
    
    protected void ProcessInputResponse()
    {
        InputHandlers?.Invoke(Input);
        AlignInput();
    }

    private void AlignInput()
    {
        if (Input == DesiredInput)
            return;
        if (DesiredInput == Input.Unknown)
            return;
        Log("Forcing Input");
        SetInput(DesiredInput);
    }

    public Input GetCurrentInput() => Input;
    
    public int GetCurrentVolume() => Volume;

    public MuteState GetAudioMute() => AudioMute;
    public MuteState GetVideoMute() => VideoMute;

    public virtual void PowerOn()
    {
        LogHandlers?.Invoke("Turning On");
        PowerState = PowerState.On;
        DesiredPowerState = PowerState.On;
        PowerStateHandlers?.Invoke(DesiredPowerState);
    }

    public virtual void PowerOff()
    {
        LogHandlers?.Invoke("Turning Off");
        PowerState = PowerState.Off;
        DesiredPowerState = PowerState.Off;
        PowerStateHandlers?.Invoke(DesiredPowerState);
    }

    public abstract void SetInput(Input input);

    public abstract void SetVolume(int volume);

    public abstract void SetAudioMute(MuteState state);

    public abstract void ToggleAudioMute();
}