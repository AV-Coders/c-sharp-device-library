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

    public Input GetCurrentInput() => Input;
    
    public int GetCurrentVolume() => Volume;

    public MuteState GetAudioMute() => AudioMute;
    public MuteState GetVideoMute() => VideoMute;

    public abstract void PowerOn();

    public abstract void PowerOff();

    public abstract void SetInput(Input input);

    public abstract void SetVolume(int volume);

    public abstract void SetAudioMute(MuteState state);

    public abstract void ToggleAudioMute();
}