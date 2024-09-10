using AVCoders.Core;
using AVCoders.Dsp;

namespace AVCoders.Conference;

public abstract class Conference : IDevice
{
    protected PowerState PowerState = PowerState.Unknown;
    protected PowerState DesiredPowerState = PowerState.Unknown;
    protected CommunicationState CommunicationState = CommunicationState.Unknown;
    public LogHandler? LogHandlers;
    public PowerStateHandler? PowerStateHandlers;
    public CommunicationStateHandler? CommunicationStateHandlers;
    public Fader OutputVolume;
    public Mute OutputMute;
    public Mute MicrophoneMute;

    public PowerState GetCurrentPowerState() => PowerState;

    public CommunicationState GetCurrentCommunicationState() => CommunicationState;

    protected readonly ThreadWorker PollWorker;
    

    protected Conference(int pollTime = 52)
    {
        OutputVolume = new Fader(_ => {}, false);
        MicrophoneMute = new Mute(_ => {});
        PollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(pollTime));
        PollWorker.Restart();
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
    
    protected abstract void Poll();

    protected void Log(string message) => LogHandlers?.Invoke($"{GetType()} - {message}");

    protected void UpdateCommunicationState(CommunicationState state)
    {
        CommunicationState = state;
        CommunicationStateHandlers?.Invoke(state);
    }
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

    public abstract void SendDtmf(char number);

    public abstract void Dial(string number);
}